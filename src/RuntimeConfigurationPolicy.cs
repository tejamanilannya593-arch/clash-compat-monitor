public static class RuntimeConfigurationPolicy
{
    public static string Ipv6Action(bool ipv6Enabled)
    {
        return ipv6Enabled
            ? "检测到 Clash 核心 IPv6 已开启；请在 Clash Verge Rev 中重新应用增强脚本后重试，程序不会重载配置"
            : null;
    }
}
