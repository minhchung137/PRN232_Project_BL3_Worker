using System.Threading;
using System.Threading.Tasks;

namespace PRN232_GradingSystem_Worker_Services.Interfaces
{
    public interface IFileDownloadService
    {
        Task<string> DownloadSubmissionZipAsync(string fileUrl, string targetPath, CancellationToken cancellationToken);
    }
}
