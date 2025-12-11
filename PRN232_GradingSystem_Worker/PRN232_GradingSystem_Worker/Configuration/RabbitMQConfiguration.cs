namespace PRN232_GradingSystem_Worker.Configuration;

/// <summary>
/// Configuration model for RabbitMQ settings
/// </summary>
public sealed class RabbitMQConfiguration
{
    public const string SectionName = "RabbitMQ";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public bool UseTls { get; set; } = false;
    public string QueueName { get; set; } = "grading-jobs";
    public int Prefetch { get; set; } = 1;
}

