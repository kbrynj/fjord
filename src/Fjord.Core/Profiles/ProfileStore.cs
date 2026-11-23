using Fjord.Core.Storage;

namespace Fjord.Core.Profiles;

/// <summary>Saved sessions in %APPDATA%\Fjord\profiles.json.</summary>
public sealed class ProfileStore
{
    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<SessionProfile> Profiles { get; set; } = [];
    }

    private readonly string _path;
    private readonly List<SessionProfile> _profiles;

    public ProfileStore(string? path = null)
    {
        _path = path ?? AppPaths.File("profiles.json");
        _profiles = JsonStore.Load<FileModel>(_path).Profiles;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SessionProfile> All =>
        _profiles.OrderBy(p => p.GroupOrDefault, StringComparer.CurrentCultureIgnoreCase)
                 .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                 .ToList();

    public IEnumerable<string> Groups =>
        _profiles.Select(p => p.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().Order();

    public SessionProfile? Find(Guid id) => _profiles.FirstOrDefault(p => p.Id == id);

    public void Upsert(SessionProfile profile)
    {
        var i = _profiles.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) _profiles[i] = profile; else _profiles.Add(profile);
        Save();
    }

    public void AddRange(IEnumerable<SessionProfile> profiles)
    {
        _profiles.AddRange(profiles);
        Save();
    }

    public void Delete(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        Save();
    }

    public void Touch(Guid id)
    {
        if (Find(id) is { } p) { p.LastConnected = DateTime.Now; Save(); }
    }

    private void Save()
    {
        JsonStore.Save(_path, new FileModel { Profiles = _profiles });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
