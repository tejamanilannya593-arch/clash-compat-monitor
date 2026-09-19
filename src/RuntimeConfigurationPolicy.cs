public static class RuntimeConfigurationPolicy
{
    public static string Ipv6Action(bool ipv6Enabled)
    {
        // An enabled stack does not prove a broken route. Service probes decide
        // health; never stop protection or rewrite configuration on this flag alone.
        return null;
    }
}
