using System.Collections.Generic;

namespace PRN232_GradingSystem_Worker_Services.Settings;

public sealed class PlaywrightTestSettings
{
    public const string SectionName = "PlaywrightTests";

    public LoginTestSettings Login { get; set; } = new();
    public PantherListTestSettings PantherList { get; set; } = new();
    public PantherCreateTestSettings PantherCreate { get; set; } = new();
    public PantherUpdateTestSettings PantherUpdate { get; set; } = new();
    public SearchTestSettings Search { get; set; } = new();
}

public sealed class LoginTestSettings
{
    public CredentialSettings InvalidCredential { get; set; } = new() { Email = "admin", Password = "123" };
    public CredentialSettings AdminCredential { get; set; } = new() { Email = "admin@Panther.com", Password = "@1" };
    public CredentialSettings ManagerCredential { get; set; } = new() { Email = "manager@Panther.com", Password = "@1" };
    public List<string> InvalidErrorKeywords { get; set; } = new() { "invalid", "wrong", "incorrect", "error", "failed" };
    public List<string> PermissionErrorKeywords { get; set; } = new() { "no permission", "permission" };
}

public sealed class CredentialSettings
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class PantherListTestSettings
{
    public List<string> TypeColumnCandidates { get; set; } = new() { "PantherType", "TypeName", "Type" };
    public Dictionary<string, List<string>> RequiredColumns { get; set; } = new()
    {
        ["PantherName"] = new() { "PantherName", "panthername", "Name", "name" },
        ["Weight"] = new() { "Weight", "weight" },
        ["Characteristics"] = new() { "Characteristics", "characteristics" },
        ["Warning"] = new() { "Warning", "warning" },
        ["ModifiedDate"] = new() { "ModifiedDate", "modifieddate", "Modified", "modified", "Date", "date" }
    };
    public string ExpectedFirstPantherName { get; set; } = "Jaguars leopards";
}

public sealed class PantherCreateTestSettings
{
    public List<string> ExpectedPantherTypeOptions { get; set; } = new() { "Black leopards", "Black jaguars", "Pumas" };
    public PantherFormData ValidationLength { get; set; } = new()
    {
        PantherTypeIndex = 1,
        PantherName = "hau",
        Weight = "91",
        Characteristics = "red",
        Warning = "no"
    };
    public PantherFormData ValidationSpecialCharacters { get; set; } = new()
    {
        PantherTypeIndex = 1,
        PantherName = "Hau@130",
        Weight = "91",
        Characteristics = "red",
        Warning = "no"
    };
    public PantherFormData ValidationRequired { get; set; } = new()
    {
        PantherTypeIndex = 1,
        PantherName = "Black Panther",
        Weight = string.Empty,
        Characteristics = "Black skin",
        Warning = "Very dangerous"
    };
    public PantherFormData AddOk { get; set; } = new()
    {
        PantherTypeIndex = 1,
        PantherName = "Black Panther",
        Weight = "91",
        Characteristics = "Black skin",
        Warning = "Very dangerous"
    };
    public string DisplayTopPantherName { get; set; } = "Black Panther";
}

public sealed class PantherUpdateTestSettings
{
    public List<string> ExpectedPantherTypeOptions { get; set; } = new() { "Black leopards", "Black jaguars", "Pumas" };
    public PantherFormData ValidationLength { get; set; } = new()
    {
        PantherTypeIndex = 0,
        PantherName = "hau",
        Weight = "200",
        Characteristics = "red",
        Warning = "warn"
    };
    public PantherFormData ValidationSpecialCharacters { get; set; } = new()
    {
        PantherTypeIndex = 0,
        PantherName = "Hau@130",
        Weight = "200",
        Characteristics = "red",
        Warning = "warn"
    };
    public PantherFormData ValidationRequiredWeight { get; set; } = new()
    {
        PantherTypeIndex = 0,
        PantherName = "Black Panther",
        Weight = string.Empty,
        Characteristics = "Black skin",
        Warning = "Very dangerous"
    };
    public PantherFormData UpdateOk { get; set; } = new()
    {
        PantherTypeIndex = 2,
        PantherName = "Update Panther",
        Weight = "300",
        Characteristics = "update panther",
        Warning = "Update Panther"
    };
    public UpdateVerificationSettings UpdateVerification { get; set; } = new()
    {
        ExpectedWeight = "300",
        ExpectedTypeValues = new() { "2", "Black jaguars" }
    };
}

public sealed class UpdateVerificationSettings
{
    public string ExpectedWeight { get; set; } = "300";
    public List<string> ExpectedTypeValues { get; set; } = new() { "2", "Black jaguars" };
}

public sealed class PantherFormData
{
    public int PantherTypeIndex { get; set; }
    public string PantherName { get; set; } = string.Empty;
    public string Weight { get; set; } = string.Empty;
    public string Characteristics { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
}

public sealed class SearchTestSettings
{
    public SearchTestCaseSettings Test1 { get; set; } = new()
    {
        Weight = "100",
        TypeName = string.Empty,
        ExpectedCountWhenDisplayTopPass = 2,
        ValidationMode = SearchValidationMode.Weight,
        AdjustByQ3DisplayTop = false
    };

    public SearchTestCaseSettings Test2 { get; set; } = new()
    {
        Weight = string.Empty,
        TypeName = "Black leopards",
        ExpectedCountWhenDisplayTopPass = 3,
        ExpectedCountWhenDisplayTopFail = 2,
        ValidationMode = SearchValidationMode.Type,
        AdjustByQ3DisplayTop = true
    };

    public SearchTestCaseSettings Test3 { get; set; } = new()
    {
        Weight = "120",
        TypeName = "Black leopards",
        ExpectedCountWhenDisplayTopPass = 3,
        ExpectedCountWhenDisplayTopFail = 2,
        ValidationMode = SearchValidationMode.WeightOrType,
        AdjustByQ3DisplayTop = true
    };
}

public sealed class SearchTestCaseSettings
{
    public string Weight { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public int ExpectedCountWhenDisplayTopPass { get; set; }
    public int? ExpectedCountWhenDisplayTopFail { get; set; }
    public bool AdjustByQ3DisplayTop { get; set; }
    public SearchValidationMode ValidationMode { get; set; } = SearchValidationMode.Weight;
}

public enum SearchValidationMode
{
    Weight,
    Type,
    WeightOrType
}

