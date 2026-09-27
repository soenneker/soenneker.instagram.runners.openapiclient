using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Instagram.Runners.OpenApiClient.Utils.Abstract;

public interface IFileOperationsUtil
{
    /// <summary>Converts Meta JSON specifications, generates and builds the publishing client, and optionally pushes when configured.</summary>
    ValueTask Process(CancellationToken cancellationToken = default);
}
