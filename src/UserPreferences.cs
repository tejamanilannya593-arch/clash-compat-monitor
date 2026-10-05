using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public sealed class UserPreferences
{
    public bool FirstRunComplete { get; set; }
    public bool AutomaticOptimization { get; set; }
    public bool AutomaticRecovery { get; set; }
    public int CheckIntervalSeconds { get; set; }
    public int FailureThreshold { get; set; }
    public int RecoveryCooldownMinutes { get; set; }
    public bool BrowserConversationVerification { get; set; }
    public List<ServiceKind> RequiredServices { get; set; }

    public UserPreferences()
    {
        AutomaticRecovery = true;
        CheckIntervalSeconds = 60;
        FailureThreshold = 2;
        RecoveryCooldownMinutes = 10;
    }

    public static UserPreferences Defaults()
    {
        return new UserPreferences {
            AutomaticOptimization = false,
            BrowserConversationVerification = false,
            RequiredServices = new List<ServiceKind> {
                ServiceKind.ChatGPT,
                ServiceKind.Gemini,
                ServiceKind.Google,
                ServiceKind.GitHub,
                ServiceKind.SteamStore,
                ServiceKind.SteamCommunity,
                ServiceKind.SteamApi
            }
        };
    }
}

public static class UserPreferencePolicy
{
    public static void NormalizeRecoverySettings(UserPreferences preferences)
    {
        if (preferences == null) throw new ArgumentNullException("preferences");
        preferences.CheckIntervalSeconds = Math.Max(15, Math.Min(3600, preferences.CheckIntervalSeconds));
        preferences.FailureThreshold = Math.Max(2, Math.Min(10, preferences.FailureThreshold));
        preferences.RecoveryCooldownMinutes = Math.Max(1, Math.Min(120, preferences.RecoveryCooldownMinutes));
    }

    public static List<ServiceKind> NormalizeServices(IEnumerable<ServiceKind> services)
    {
        return CoreWebsitePolicy.Normalize(services);
    }
}

public sealed class UserPreferenceStore
{
    private readonly string path;

    public UserPreferenceStore(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A preferences path is required.", "path");
        this.path = path;
    }

    public UserPreferences Load()
    {
        if (!File.Exists(path)) return UserPreferences.Defaults();
        try
        {
            var values = File.ReadAllLines(path, Encoding.UTF8)
                .Select(line => line.Split(new[] { '=' }, 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
            int version;
            bool firstRun;
            bool automatic;
            string versionValue;
            string firstRunValue;
            string automaticValue;
            string serviceValue;
            if (!values.TryGetValue("version", out versionValue) || !Int32.TryParse(versionValue, out version) ||
                (version < 1 || version > 4) ||
                !values.TryGetValue("firstRun", out firstRunValue) || !Boolean.TryParse(firstRunValue, out firstRun) ||
                !values.TryGetValue("automatic", out automaticValue) || !Boolean.TryParse(automaticValue, out automatic) ||
                !values.TryGetValue("services", out serviceValue)) throw new InvalidDataException("Preferences are incomplete.");
            var services = new List<ServiceKind>();
            foreach (string name in serviceValue.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (String.Equals(name, "JMComicWeb", StringComparison.Ordinal)) continue;
                ServiceKind service;
                if (!Enum.TryParse(name, false, out service) || !Enum.IsDefined(typeof(ServiceKind), service))
                    throw new InvalidDataException("Preferences contain an unknown service.");
                if (!services.Contains(service)) services.Add(service);
            }
            services = UserPreferencePolicy.NormalizeServices(services);
            var preferences = new UserPreferences {
                FirstRunComplete = firstRun,
                AutomaticOptimization = automatic,
                BrowserConversationVerification = false,
                RequiredServices = services
            };
            string setting;
            bool enabled;
            int number;
            if (values.TryGetValue("automaticRecovery", out setting) && Boolean.TryParse(setting, out enabled))
                preferences.AutomaticRecovery = enabled;
            if (values.TryGetValue("checkIntervalSeconds", out setting) && Int32.TryParse(setting, out number))
                preferences.CheckIntervalSeconds = number;
            if (values.TryGetValue("failureThreshold", out setting) && Int32.TryParse(setting, out number))
                preferences.FailureThreshold = number;
            if (values.TryGetValue("recoveryCooldownMinutes", out setting) && Int32.TryParse(setting, out number))
                preferences.RecoveryCooldownMinutes = number;
            UserPreferencePolicy.NormalizeRecoverySettings(preferences);
            return preferences;
        }
        catch
        {
            ArchiveCorruptFile();
            return UserPreferences.Defaults();
        }
    }

    public void Save(UserPreferences value)
    {
        if (value == null || value.RequiredServices == null)
            throw new ArgumentException("At least one service is required.", "value");
        var services = value.RequiredServices.Distinct().ToList();
        if (services.Any(service => !Enum.IsDefined(typeof(ServiceKind), service)))
            throw new ArgumentException("Preferences contain an unknown service.", "value");
        services = UserPreferencePolicy.NormalizeServices(services);
        UserPreferencePolicy.NormalizeRecoverySettings(value);

        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        string text = "version=4" + Environment.NewLine +
            "firstRun=" + value.FirstRunComplete + Environment.NewLine +
            "automatic=" + value.AutomaticOptimization + Environment.NewLine +
            "automaticRecovery=" + value.AutomaticRecovery + Environment.NewLine +
            "checkIntervalSeconds=" + value.CheckIntervalSeconds + Environment.NewLine +
            "failureThreshold=" + value.FailureThreshold + Environment.NewLine +
            "recoveryCooldownMinutes=" + value.RecoveryCooldownMinutes + Environment.NewLine +
            "services=" + String.Join(",", services.Select(service => service.ToString())) + Environment.NewLine;
        File.WriteAllText(temporary, text, Encoding.UTF8);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private void ArchiveCorruptFile()
    {
        if (!File.Exists(path)) return;
        string archive = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        File.Move(path, archive);
    }
}
