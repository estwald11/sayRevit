using System.Threading;
using System.Threading.Tasks;
using SayRevit.Core.Model;

namespace SayRevit.Core.Parsing
{
    /// <summary>Trasforma una descrizione in linguaggio naturale in un <see cref="MepPlan"/>.</summary>
    public interface IIntentParser
    {
        string Name { get; }
        Task<ParseResult> ParseAsync(string text, ModelCatalog catalog, CancellationToken cancellationToken);
    }
}
