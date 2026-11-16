using System;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ComponentInterfaces;

namespace ChargeTweaks
{
    /// <summary>
    /// 服务端模组入口。
    ///
    /// 安装思路：
    ///   1) InitializeGameStarter 里通过 IGameStarter.AddModel 把 ChargeDamageModel 注册进
    ///      游戏模型列表；MissionGameModels 构造时会用 GetGameModel&lt;AgentApplyDamageModel&gt;()
    ///      从列表尾部往前找，最后加入的模型优先，因此我们的模型会生效。
    ///   2) 每张地图初始化时做一次兜底检查：若当前模型不是我们的，则用反射写
    ///      MissionGameModels 的私有后备字段，确保倍率一定生效。
    ///
    /// 注意：MissionGameModels.AgentApplyDamageModel 的 setter 是私有的，不能直接赋值；
    /// 本版本 MBSubModuleBase.OnSubModuleLoad 是 protected internal 且不在公共钩子里，
    /// 因此配置采用懒加载。
    /// </summary>
    public class ChargeTweaksSubModule : MBSubModuleBase
    {
        public const string Version = "1.1.0";

        private static readonly FieldInfo DamageModelField =
            typeof(MissionGameModels).GetField("<AgentApplyDamageModel>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        protected override void InitializeGameStarter(Game game, IGameStarter gameStarter)
        {
            base.InitializeGameStarter(game, gameStarter);

            ChargeConfig.EnsureLoaded();

            try
            {
                if (gameStarter == null)
                {
                    Console.WriteLine("[ChargeTweaks] InitializeGameStarter: gameStarter 为空，跳过注册");
                    return;
                }

                gameStarter.AddModel(new ChargeDamageModel());
                Console.WriteLine("[ChargeTweaks] 已注册 ChargeDamageModel | 玩家倍率=" + ChargeConfig.PlayerMultiplier + " AI倍率=" + ChargeConfig.BotMultiplier);
            }
            catch (Exception e)
            {
                Console.WriteLine("[ChargeTweaks] InitializeGameStarter 注册失败（将走运行时兜底）：" + e);
            }
        }

        public override void OnMultiplayerGameStart(Game game, object initializerObject)
        {
            base.OnMultiplayerGameStart(game, initializerObject);
            TryInstallFallback("OnMultiplayerGameStart");
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);

            if (!GameNetwork.IsServer)
            {
                return;
            }

            ChargeConfig.EnsureLoaded();
            TryInstallFallback("OnMissionBehaviorInitialize");
        }

        /// <summary>兜底：直接把当前 MissionGameModels 的伤害模型替换掉。</summary>
        private static void TryInstallFallback(string source)
        {
            try
            {
                MissionGameModels models = MissionGameModels.Current;
                if (models == null)
                {
                    return;
                }

                if (models.AgentApplyDamageModel is ChargeDamageModel)
                {
                    return; // 通过 AddModel 注册的路径已生效
                }

                if (DamageModelField == null)
                {
                    Console.WriteLine("[ChargeTweaks] " + source + ": 找不到 AgentApplyDamageModel 后备字段，兜底失败");
                    return;
                }

                DamageModelField.SetValue(models, new ChargeDamageModel());
                Console.WriteLine("[ChargeTweaks] " + source + ": 已强制替换伤害模型 | 玩家倍率=" + ChargeConfig.PlayerMultiplier + " AI倍率=" + ChargeConfig.BotMultiplier);
            }
            catch (Exception e)
            {
                Console.WriteLine("[ChargeTweaks] " + source + ": 兜底替换失败：" + e);
            }
        }
    }
}
