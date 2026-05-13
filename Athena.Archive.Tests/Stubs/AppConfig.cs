namespace Athena.UI.Models;

public class AppConfig
{
    public string Provider { get; set; } = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string SecondaryProvider { get; set; } = "OpenAI";

    public string SecondaryApiKey { get; set; } = string.Empty;

    public string SecondaryBaseUrl { get; set; } = string.Empty;

    public string SecondaryModel { get; set; } = "gpt-4o-mini";
}
