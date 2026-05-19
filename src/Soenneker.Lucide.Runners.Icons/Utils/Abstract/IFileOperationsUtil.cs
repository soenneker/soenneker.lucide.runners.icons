using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Lucide.Runners.Icons.Utils.Abstract;

public interface IFileOperationsUtil
{
    ValueTask Process(CancellationToken cancellationToken);
}
