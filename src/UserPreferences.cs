using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public sealed class UserPreferences
{
    public bool FirstRunComplete { get; set; }
    public bool AutomaticOptimization { get; set; }
    public List<ServiceKind> RequiredServices { get; set; }

    public static UserPreferences Defaults()
    {
        return new UserPreferences {
            AutomaticOptimization = true,
            RequiredServices = new List<ServiceKind> {
                ServiceKind.Google,
                ServiceKind.GitHub,
                ServiceKind.ChatGPT,
                ServiceKind.Gemini,
                ServiceKind.SteamStore,
                ServiceKind.SteamCommunity,
                ServiceKind.SteamApi
            }
        };
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
            bool firstRun;
            bool automatic;
            string firstRunValue;
            string automaticValue;
            string serviceValue;
            if (!values.TryGetValue("firstRun", out firstRunValue) || !Boolean.TryParse(firstRunValue, out firstRun) ||
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
            if (services.Count == 0) throw new InvalidDataException("At least one service is required.");
            return new UserPreferences {
                FirstRunComplete = firstRun,
                AutomaticOptimization = automatic,
                RequiredServices = services
            };
        }
        catch
        {
            ArchiveCorruptFile();
            return UserPreferences.Defaults();
        }
    }

    public void Save(UserPreferences value)
    {
        if (value == null || value.RequiredServices == null || value.RequiredServices.Count == 0)
            throw new ArgumentException("At least one service is required.", "value");
        var services = value.RequiredServices.Distinct().ToList();
        if (services.Any(service => !Enum.IsDefined(typeof(ServiceKind), service)))
            throw new ArgumentException("Preferences contain an unknown service.", "value");

        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        string text = "version=1" + Environment.NewLine +
            "firstRun=" + value.FirstRunComplete + Environment.NewLine +
            "automatic=" + value.AutomaticOptimization + Environment.NewLine +
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
