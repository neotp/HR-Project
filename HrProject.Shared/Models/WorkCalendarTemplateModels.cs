namespace HrProject.Shared.Models;

public sealed class WorkCalendarTemplateSettings
{
    public string HeaderBandColor { get; set; } = "#f31322";
    public string AccentColor { get; set; } = "#001b80";
    public string TitleColor { get; set; } = "#111827";
    public bool ShowLogo { get; set; } = true;
    public string LogoPosition { get; set; } = "RIGHT";
    public string TitleAlignment { get; set; } = "CENTER";
    public string TitleThai { get; set; } = string.Empty;
    public string TitleEnglish { get; set; } = string.Empty;
    public bool ShowEnglishTitle { get; set; } = true;
    public string IntroText { get; set; } = string.Empty;
    public string PolicyText { get; set; } = string.Empty;
    public string FooterThai { get; set; } = string.Empty;
    public string FooterEnglish { get; set; } = string.Empty;
    public bool ShowFooter { get; set; } = true;
    public decimal PageMarginMm { get; set; } = 17;
    public decimal BaseFontSizePt { get; set; } = 9.2m;
    public decimal ListSpacingMm { get; set; } = 2.2m;
}

public sealed record WorkCalendarTemplateDto(
    long Id,
    string TemplateType,
    string TemplateName,
    int Version,
    bool IsPublished,
    WorkCalendarTemplateSettings Settings,
    string CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAt);

public sealed record SaveWorkCalendarTemplateRequest(
    string TemplateType,
    string TemplateName,
    WorkCalendarTemplateSettings Settings);
