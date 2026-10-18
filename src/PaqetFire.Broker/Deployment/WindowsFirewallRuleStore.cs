namespace PaqetFire.Broker.Deployment;

public sealed class WindowsFirewallRuleStore : IFirewallRuleStore
{
    private const int InboundDirection = 1;
    private const int AllowAction = 1;
    private const int AllProfiles = 0x7FFFFFFF;
    private const int MaximumDuplicateRemovals = 16;

    public IReadOnlyList<FirewallRule> FindByName(string name)
    {
        // INetFwRules.Item returns only the first rule with a name, so duplicates need enumeration.
        var matches = new List<FirewallRule>();
        foreach (dynamic rule in CreatePolicy().Rules)
        {
            if (string.Equals((string?)rule.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new FirewallRule(
                    (string)rule.Name,
                    (string?)rule.Description ?? string.Empty,
                    (string?)rule.ApplicationName ?? string.Empty,
                    (FirewallProtocol)(int)rule.Protocol,
                    (string?)rule.RemoteAddresses ?? string.Empty,
                    (bool)rule.Enabled,
                    (int)rule.Direction == InboundDirection &&
                    (int)rule.Action == AllowAction &&
                    ((int)rule.Profiles & AllProfiles) == AllProfiles));
            }
        }

        return matches;
    }

    public void Replace(FirewallRule rule)
    {
        var rules = CreatePolicy().Rules;
        for (var removal = 0; removal < MaximumDuplicateRemovals && FindByName(rule.Name).Count > 0; removal++)
        {
            rules.Remove(rule.Name);
        }

        dynamic created = Activator.CreateInstance(
            Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        created.Name = rule.Name;
        created.Description = rule.Description;
        created.ApplicationName = rule.ProgramPath;
        created.Protocol = (int)rule.Protocol;
        created.RemoteAddresses = rule.RemoteAddresses;
        created.Direction = InboundDirection;
        created.Action = AllowAction;
        created.Profiles = AllProfiles;
        created.Enabled = true;
        rules.Add(created);
    }

    private static dynamic CreatePolicy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
}
