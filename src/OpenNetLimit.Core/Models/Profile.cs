namespace OpenNetLimit.Core.Models;

public class Profile
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public List<Guid> RuleIds { get; set; } = [];
}
