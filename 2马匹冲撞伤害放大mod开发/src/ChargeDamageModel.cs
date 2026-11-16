using System;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ComponentInterfaces;

namespace ChargeTweaks
{
    /// <summary>
    /// 替换服务端的 AgentApplyDamageModel，只在“坐骑冲撞”的最终伤害上乘倍率。
    ///
    /// 调用链（已通过 IL 反汇编确认）：
    ///   Mission.ChargeDamageCallback
    ///     -> Mission.GetAttackCollisionResults
    ///       -> AgentApplyDamageModel.CalculateDamage          (不可重写)
    ///            -> IsDamageIgnored                          (虚)
    ///            -> ApplyDamageAmplifications                (虚)
    ///            -> ApplyDamageScaling                       (虚)
    ///            -> ApplyDamageReductions                    (虚)
    ///            -> ApplyGeneralDamageModifiers              (虚) ← 本类重写这里
    /// 冲撞回调传入的攻击者就是“马”（Agent.IsMount == true），
    /// 因此 AttackInformation.IsAttackerAgentMount 为 true，可精确命中马撞人，
    /// 不会影响近战、远程和普通骑乘挥砍。
    ///
    /// v1.1.0 的稳定性改进（目的：压低“单位时间击杀/重生事件密度”）：
    ///   1) 真人骑手与 AI 骑手分开倍率；默认 AI = 1.0（不改），只有玩家的马才 20 倍；
    ///   2) 可选忽略友伤（默认忽略）；
    ///   3) 每秒最多放大 N 次，避免一次冲锋瞬间刷出大量死亡事件；
    ///   4) 已死亡/已移除的受害者不再放大；NaN/Inf 与可选伤害上限保护。
    /// </summary>
    public sealed class ChargeDamageModel : MultiplayerAgentApplyDamageModel
    {
        private readonly object _sync = new object();
        private Mission _windowMission;
        private float _windowStart = -1f;
        private int _windowHits;

        public override float ApplyGeneralDamageModifiers(
            in AttackInformation attackInformation,
            in AttackCollisionData collisionData,
            float baseMagnitude)
        {
            float damage = base.ApplyGeneralDamageModifiers(attackInformation, collisionData, baseMagnitude);

            // NaN / Inf / 0 / 负值：一律不改（!(x > 0) 同时挡掉 NaN）
            if (!(damage > 0f) || float.IsInfinity(damage))
            {
                return damage;
            }

            if (!IsMountCharge(in attackInformation))
            {
                return damage;
            }

            if (attackInformation.IsVictimAgentNull)
            {
                return damage;
            }

            Agent victim = attackInformation.VictimAgent;
            if (victim == null || !victim.IsActive())
            {
                // 目标已经倒地/被移除：不再参与放大，避免“对已死 agent 追加伤害”
                return damage;
            }

            if (!ChargeConfig.AffectFriendlyFire && attackInformation.IsFriendlyFire)
            {
                return damage;
            }

            bool playerRider = IsPlayerRiddenMount(in attackInformation);
            float multiplier = playerRider ? ChargeConfig.PlayerMultiplier : ChargeConfig.BotMultiplier;

            if (multiplier <= 0f || multiplier == 1f)
            {
                return damage;
            }

            if (!TryTakeBudget())
            {
                if (ChargeConfig.LogDamage)
                {
                    Console.WriteLine(string.Format(
                        "[ChargeTweaks] 冲撞被限流（超过每秒 {0} 次），按原版伤害结算 | 受害者={1}",
                        ChargeConfig.MaxEmpoweredHitsPerSecond,
                        DescribeAgent(victim)));
                }

                return damage;
            }

            float scaled = damage * multiplier;
            if (!(scaled > 0f) || float.IsInfinity(scaled))
            {
                return damage;
            }

            float cap = ChargeConfig.MaxDamage;
            if (cap > 0f && scaled > cap)
            {
                scaled = cap;
            }

            if (ChargeConfig.LogDamage)
            {
                Console.WriteLine(string.Format(
                    "[ChargeTweaks] 冲撞伤害 {0:F1} -> {1:F1} (x{2}, {3}) | 受害者={4}",
                    damage,
                    scaled,
                    multiplier,
                    playerRider ? "玩家骑手" : "AI骑手",
                    DescribeAgent(victim)));
            }

            return scaled;
        }

        /// <summary>
        /// 判断这一击是不是骑乘冲撞：
        /// 主判定 IsAttackerAgentMount（冲撞时攻击者就是马）；
        /// 次判定兼容“攻击者是骑手、本次属于被动攻击”的实现差异。
        /// </summary>
        private static bool IsMountCharge(in AttackInformation info)
        {
            if (info.IsAttackerAgentMount)
            {
                return true;
            }

            return info.IsAttackerAgentDoingPassiveAttack && info.DoesAttackerHaveMountAgent;
        }

        /// <summary>
        /// 判断这次冲撞的坐骑是不是由真人玩家在骑。
        /// 服务端上：真人 agent 带 MissionPeer / 非 AI 控制；机器人是 IsAIControlled。
        /// 取不到骑手时退回到 AttackInformation.IsAttackerPlayer。
        /// </summary>
        private static bool IsPlayerRiddenMount(in AttackInformation info)
        {
            Agent attacker = info.AttackerAgent;
            if (attacker != null)
            {
                Agent rider = attacker.RiderAgent;
                if (rider != null)
                {
                    return IsHumanControlled(rider);
                }

                // 马自己没有骑手时，用“归属玩家”兜底（例如骑手刚下马/上马的瞬间）
                try
                {
                    if (attacker.OwningAgentMissionPeer != null)
                    {
                        return true;
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            return info.IsAttackerPlayer;
        }

        private static bool IsHumanControlled(Agent agent)
        {
            if (agent == null)
            {
                return false;
            }

            try
            {
                if (agent.MissionPeer != null)
                {
                    return true;
                }

                if (agent.IsPlayerControlled)
                {
                    return true;
                }

                return !agent.IsAIControlled;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 每秒放大次数预算。0 = 不限制。
        /// 用 Mission.CurrentTime 做 1 秒滑动窗口，跨地图/任务自动重置。
        /// </summary>
        private bool TryTakeBudget()
        {
            int limit = ChargeConfig.MaxEmpoweredHitsPerSecond;
            if (limit <= 0)
            {
                return true;
            }

            Mission mission = Mission.Current;
            float now = mission != null ? mission.CurrentTime : 0f;

            lock (_sync)
            {
                bool newWindow = !ReferenceEquals(mission, _windowMission)
                                 || now < _windowStart
                                 || (now - _windowStart) >= 1f;

                if (newWindow)
                {
                    _windowMission = mission;
                    _windowStart = now;
                    _windowHits = 0;
                }

                if (_windowHits >= limit)
                {
                    return false;
                }

                _windowHits++;
                return true;
            }
        }

        private static string DescribeAgent(Agent agent)
        {
            if (agent == null)
            {
                return "<null>";
            }

            try
            {
                return agent.Name ?? "<unnamed>";
            }
            catch
            {
                return "<agent>";
            }
        }
    }
}
