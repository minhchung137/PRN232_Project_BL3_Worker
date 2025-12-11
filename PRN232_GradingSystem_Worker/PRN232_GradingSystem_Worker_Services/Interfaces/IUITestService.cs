using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PRN232_GradingSystem_Worker_Services.Models;

namespace PRN232_GradingSystem_Worker_Services.Interfaces
{
    public interface IUITestService
    {
        Task<(int Total, int Passed, TestResultDetail? Detail)> RunAsync(string baseUrl, CancellationToken cancellationToken);
    }
}


