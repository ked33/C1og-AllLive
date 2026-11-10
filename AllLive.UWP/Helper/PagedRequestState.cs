using System;
using System.Threading;

namespace AllLive.UWP.Helper
{
    // Owned by the page's UI thread. Retired requests keep their token until their
    // finally block runs, so a late completion can never finish a newer request.
    internal sealed class PagedRequestState
    {
        private Request active;
        internal int Page { get; private set; } = 1;

        internal Request TryBegin()
        {
            if (active != null) return null;
            active = new Request(Page);
            return active;
        }

        internal bool IsCurrent(Request request)
        {
            return ReferenceEquals(active, request) && !request.Token.IsCancellationRequested;
        }

        internal void Accept(Request request)
        {
            if (IsCurrent(request)) Page = request.Page + 1;
        }

        internal void End(Request request)
        {
            if (ReferenceEquals(active, request)) active = null;
            request.Dispose();
        }

        internal void Cancel()
        {
            var retired = active;
            active = null;
            retired?.Cancel();
        }

        internal void Reset()
        {
            Cancel();
            Page = 1;
        }

        internal sealed class Request : IDisposable
        {
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            internal int Page { get; }
            internal CancellationToken Token { get; }

            internal Request(int page)
            {
                Page = page;
                Token = cancellation.Token;
            }

            internal void Cancel() => cancellation.Cancel();
            public void Dispose() => cancellation.Dispose();
        }
    }
}
