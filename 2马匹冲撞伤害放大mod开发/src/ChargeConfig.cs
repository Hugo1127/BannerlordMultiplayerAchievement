using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml;

namespace ChargeTweaks
{
    /// <summary>
    /// 读取 Modules/ChargeTweaks/charge_config.xml。
    ///
    /// v1.1.0 起把「冲撞放大」拆成几个可控旋钮，核心目的是降低单位时间内的
    /// 击杀/重生事件密度（事件越密，越容易撞上客户端的 agent 索引竞态）：
    ///   - PlayerMultiplier / BotMultiplier：真人骑手与 AI 骑手分开倍率；
    ///   - AffectFriendlyFire：友伤是否也放大；
    ///   - MaxEmpoweredHitsPerSecond：每秒最多放大几次（0 = 不限制）；
    ///   - MaxDamage：放大后的伤害上限（0 = 不限制）。
    ///
    /// 任何异常或非法值都回退到默认值，绝不让服务器启动失败。
    /// </summary>
    public static class ChargeConfig
    {
        public const float DefaultPlayerMultiplier = 20f;
        public const float DefaultBotMultiplier = 1f;
        public const bool DefaultAffectFriendlyFire = false;
        public const int DefaultMaxEmpoweredHitsPerSecond = 8;

        /// <summary>真人骑手（玩家）的冲撞伤害倍率。</summary>
        public static float PlayerMultiplier { get; private set; } = DefaultPlayerMultiplier;

        /// <summary>AI 骑手（机器人）的冲撞伤害倍率；1.0 = 与原版一致。</summary>
        public static float BotMultiplier { get; private set; } = DefaultBotMultiplier;

        /// <summary>友伤是否也放大。</summary>
        public static bool AffectFriendlyFire { get; private set; } = DefaultAffectFriendlyFire;

        /// <summary>每秒最多放大多少次冲撞；0 表示不限制。</summary>
        public static int MaxEmpoweredHitsPerSecond { get; private set; } = DefaultMaxEmpoweredHitsPerSecond;

        /// <summary>放大后的伤害上限；0 表示不限制。</summary>
        public static float MaxDamage { get; private set; }

        public static bool LogDamage { get; private set; }

        public static string ConfigPath { get; private set; } = "<未解析>";

        private static bool _loaded;

        /// <summary>首次调用时读取配置，之后直接复用（避免每次命中都读文件）。</summary>
        public static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            Load();
        }

        public static void Load()
        {
            string dir = ResolveModuleDirectory();
            if (dir == null)
            {
                Console.WriteLine("[ChargeTweaks] 找不到模块目录，使用默认配置");
                LogCurrent();
                return;
            }

            ConfigPath = Path.Combine(dir, "charge_config.xml");
            LoadFrom(ConfigPath);
        }

        /// <summary>从指定路径解析配置，供启动时加载与自检使用。</summary>
        public static void LoadFrom(string path)
        {
            _loaded = true;
            ConfigPath = path;

            float player = DefaultPlayerMultiplier;
            float bot = DefaultBotMultiplier;
            bool friendlyFire = DefaultAffectFriendlyFire;
            int hitsPerSecond = DefaultMaxEmpoweredHitsPerSecond;
            float maxDamage = 0f;
            bool logDamage = false;

            try
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine("[ChargeTweaks] 配置不存在: " + path + "，使用默认配置");
                }
                else
                {
                    var doc = new XmlDocument();
                    doc.Load(path);

                    XmlNode root = doc.SelectSingleNode("/ChargeTweaksConfig");
                    if (root != null)
                    {
                        // 旧版配置只有 <Multiplier>：保持旧行为（玩家与 AI 同倍率）。
                        float legacy;
                        bool hasLegacy = TryFloat(root.SelectSingleNode("Multiplier"), 0f, 1000f, out legacy);

                        float parsed;
                        if (TryFloat(root.SelectSingleNode("PlayerMultiplier"), 0f, 1000f, out parsed))
                        {
                            player = parsed;
                        }
                        else if (hasLegacy)
                        {
                            player = legacy;
                        }

                        if (TryFloat(root.SelectSingleNode("BotMultiplier"), 0f, 1000f, out parsed))
                        {
                            bot = parsed;
                        }
                        else if (hasLegacy)
                        {
                            bot = legacy;
                        }

                        XmlNode ff = root.SelectSingleNode("AffectFriendlyFire");
                        if (ff != null)
                        {
                            friendlyFire = string.Equals(ff.InnerText.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                        }

                        int parsedInt;
                        if (TryInt(root.SelectSingleNode("MaxEmpoweredHitsPerSecond"), 0, 1000, out parsedInt))
                        {
                            hitsPerSecond = parsedInt;
                        }

                        if (TryFloat(root.SelectSingleNode("MaxDamage"), 0f, 1000000f, out parsed))
                        {
                            maxDamage = parsed;
                        }

                        XmlNode logNode = root.SelectSingleNode("LogDamage");
                        if (logNode != null)
                        {
                            logDamage = string.Equals(logNode.InnerText.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                player = DefaultPlayerMultiplier;
                bot = DefaultBotMultiplier;
                friendlyFire = DefaultAffectFriendlyFire;
                hitsPerSecond = DefaultMaxEmpoweredHitsPerSecond;
                maxDamage = 0f;
                logDamage = false;
                Console.WriteLine("[ChargeTweaks] 配置读取出错，使用默认配置：" + e.Message);
            }

            PlayerMultiplier = player;
            BotMultiplier = bot;
            AffectFriendlyFire = friendlyFire;
            MaxEmpoweredHitsPerSecond = hitsPerSecond;
            MaxDamage = maxDamage;
            LogDamage = logDamage;

            LogCurrent();
        }

        private static void LogCurrent()
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[ChargeTweaks] 配置: 玩家倍率={0} AI倍率={1} 友伤放大={2} 每秒上限={3} 伤害上限={4} 日志={5} | {6}",
                PlayerMultiplier,
                BotMultiplier,
                AffectFriendlyFire,
                MaxEmpoweredHitsPerSecond,
                MaxDamage,
                LogDamage,
                ConfigPath));
        }

        private static bool TryFloat(XmlNode node, float minExclusive, float maxInclusive, out float value)
        {
            value = 0f;
            if (node == null || node.InnerText == null)
            {
                return false;
            }

            float parsed;
            if (!float.TryParse(node.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
            {
                return false;
            }

            value = parsed;
            return parsed > minExclusive && parsed <= maxInclusive;
        }

        private static bool TryInt(XmlNode node, int minInclusive, int maxInclusive, out int value)
        {
            value = 0;
            if (node == null || node.InnerText == null)
            {
                return false;
            }

            int parsed;
            if (!int.TryParse(node.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            value = parsed;
            return parsed >= minInclusive && parsed <= maxInclusive;
        }

        private static string ResolveModuleDirectory()
        {
            // 1) 正常情况：模块系统已经初始化，直接用官方 API
            try
            {
                string path = TaleWorlds.ModuleManager.ModuleHelper.GetModuleFullPath("ChargeTweaks");
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // 忽略，走后备方案
            }

            // 2) 后备：DLL 位于 Modules/ChargeTweaks/bin/Win64_Shipping_Server/，往上三层即模块目录
            try
            {
                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(assemblyDir))
                {
                    string candidate = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", ".."));
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return null;
        }
    }
}
