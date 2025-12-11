using Microsoft.EntityFrameworkCore;
using PRN232_GradingSystem_Worker.Extensions;
using PRN232_GradingSystem_Worker.Hosted;
using PRN232_GradingSystem_Worker_Repo.DBContext;

var builder = Host.CreateApplicationBuilder(args);

// Reduce EF Core SQL and connection logs; keep warnings/errors
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Connection", LogLevel.Warning);

// Register configuration sections
builder.Services.AddRabbitMQConfiguration(builder.Configuration);
builder.Services.AddBackblazeConfiguration(builder.Configuration);
builder.Services.AddGradingConfiguration(builder.Configuration);
builder.Services.AddCallbackApiConfiguration(builder.Configuration);

// Register application services
builder.Services.AddDatabaseServices(builder.Configuration);
builder.Services.AddFileDownloadServices();
builder.Services.AddCallbackService(builder.Configuration);
builder.Services.AddUiTestingServices(builder.Configuration);
builder.Services.AddProcessTrackerService();
builder.Services.AddGradingPipeline(builder.Configuration);

// Add DbContext
builder.Services.AddDbContext<PRN232_Grading_System_GradingContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQLConnection")));

// Register duplicate detection services
builder.Services.AddDuplicateDetectionServices();

// Register hosted services
builder.Services.AddHostedService<GradingConsumerService>();

var host = builder.Build();
host.Run();
