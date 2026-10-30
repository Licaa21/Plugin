using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Services;
using Moonfin.Games;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Sends a PlayStation disc set as an uncompressed zip written as it goes out, so there's no
    /// size limit and every track arrives.
    /// </summary>
    internal sealed class DiscSetZipResult : IAsyncStreamWriter
    {
        private readonly DiscSet _discSet;

        public DiscSetZipResult(DiscSet discSet)
        {
            _discSet = discSet;
        }

        public Task WriteToAsync(IResponse response, CancellationToken cancellationToken)
        {
            response.ContentType = "application/zip";
            response.SetContentLength(_discSet.Length);

            // The zip is written synchronously, so it runs off the request thread.
            var output = response.OutputWriter.AsStream(leaveOpen: true);
            return Task.Run(() => _discSet.WriteTo(output), cancellationToken);
        }
    }
}
