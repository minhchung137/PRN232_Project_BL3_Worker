using System.Threading;
using System.Threading.Tasks;

namespace PRN232_GradingSystem_Worker_Services.Interfaces
{
    public interface IDbResetService
    {
        Task ResetAsync(string sqlScript, CancellationToken cancellationToken);
    }
}
