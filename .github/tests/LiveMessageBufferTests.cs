using AllLive.Core.Models;
using AllLive.UWP.Helper;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class LiveMessageBufferTests
    {
        public static string Run()
        {
            var source = new object();
            var buffer = new LiveMessageBuffer(3);
            buffer.Enqueue(source, Chat("old"));
            buffer.Enqueue(source, new LiveMessage { Type = LiveMessageType.SuperChat, Message = "paid" });
            buffer.Enqueue(source, Chat("middle"));
            buffer.Enqueue(source, Chat("new"));
            var batch = buffer.Drain(8);
            Assert(batch.Select(x => x.Message.Message).SequenceEqual(new[] { "paid", "middle", "new" }),
                "Overflow must preserve SC and the retained message order.");
            Assert(batch.All(x => ReferenceEquals(x.Source, source)), "Source identity was lost.");
            for (var i = 0; i < 1000; i++) buffer.Enqueue(source, Metric(i, LiveAudienceMetricKind.ViewerCount));
            buffer.Enqueue(source, Metric(7, LiveAudienceMetricKind.VipCount));
            batch = buffer.Drain(8);
            Assert(batch.Count == 2 && batch.Any(x => (int)x.Message.Data == 999) &&
                batch.Any(x => (int)x.Message.Data == 7), "Latest independent audience metrics were not coalesced.");
            buffer.Enqueue(source, Metric(1, LiveAudienceMetricKind.ViewerCount));
            buffer.Enqueue(source, Metric(2, LiveAudienceMetricKind.Popularity));
            Assert(buffer.Drain(1).Count == 1 && buffer.HasItems && buffer.Drain(1).Count == 1,
                "Audience updates bypassed the batch limit or were lost between batches.");
            Parallel.For(0, 5000, i => buffer.Enqueue(source, Chat(i.ToString())));
            Assert(buffer.Drain(2).Count == 2 && buffer.Drain(8).Count == 1 && !buffer.HasItems,
                "Concurrent producers bypassed the queue capacity or drain budget.");
            for (var i = 0; i < 100; i++) buffer.Enqueue(source, new LiveMessage { Type = LiveMessageType.SuperChat });
            Assert(buffer.Drain(100).Count == 3, "Paid-message overload bypassed the queue bound.");
            buffer.Enqueue(source, Chat("discard"));
            buffer.Enqueue(source, Metric(1, LiveAudienceMetricKind.Popularity));
            buffer.Clear();
            Assert(!buffer.HasItems, "Closing a room retained pending messages.");
            var paid = new LiveSuperChatMessage { Id = "42", Message = "original" };
            var reload = new LiveSuperChatMessage { Id = "42", Message = "changed" };
            Assert(LiveMessageBuffer.GetSuperChatKey(paid) == LiveMessageBuffer.GetSuperChatKey(reload),
                "Reloaded SC must deduplicate by platform ID.");
            paid.Id = reload.Id = null;
            Assert(LiveMessageBuffer.GetSuperChatKey(paid) != LiveMessageBuffer.GetSuperChatKey(reload),
                "Fallback keys merged distinct paid messages.");
            return "PASS: bounded concurrent chat buffering, paid-message priority, metric coalescing and SC identities.";
        }

        private static LiveMessage Chat(string text) => new LiveMessage { Type = LiveMessageType.Chat, Message = text };
        private static LiveMessage Metric(int value, LiveAudienceMetricKind kind) =>
            new LiveMessage { Type = LiveMessageType.Online, Data = value, AudienceMetricKind = kind };
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
