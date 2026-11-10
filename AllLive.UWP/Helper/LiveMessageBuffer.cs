using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AllLive.UWP.Helper
{
    // Bound work before it reaches the UI dispatcher. Audience metrics are independent
    // (viewers, VIPs and popularity must not overwrite each other).
    public sealed class LiveMessageBuffer
    {
        public sealed class Entry
        {
            public object Source { get; set; }
            public LiveMessage Message { get; set; }
        }

        private readonly object sync = new object();
        private readonly LinkedList<Entry> messages = new LinkedList<Entry>();
        private readonly Dictionary<LiveAudienceMetricKind, Entry> metrics = new Dictionary<LiveAudienceMetricKind, Entry>();
        private readonly int capacity;

        public LiveMessageBuffer(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        public bool HasItems
        {
            get { lock (sync) return messages.Count != 0 || metrics.Count != 0; }
        }

        public void Enqueue(object source, LiveMessage message)
        {
            if (message == null) return;
            var entry = new Entry { Source = source, Message = message };
            lock (sync)
            {
                if (message.Type == LiveMessageType.Online)
                {
                    metrics[message.AudienceMetricKind] = entry;
                    return;
                }
                if (message.Type != LiveMessageType.Chat && message.Type != LiveMessageType.SuperChat) return;
                if (messages.Count >= capacity)
                {
                    // Retain paid messages ahead of ordinary chat under overload.
                    var discard = messages.First;
                    while (discard != null && discard.Value.Message.Type == LiveMessageType.SuperChat)
                        discard = discard.Next;
                    if (discard == null && message.Type != LiveMessageType.SuperChat) return;
                    messages.Remove(discard ?? messages.First);
                }
                messages.AddLast(entry);
            }
        }

        public List<Entry> Drain(int maxMessages)
        {
            if (maxMessages < 1) throw new ArgumentOutOfRangeException(nameof(maxMessages));
            lock (sync)
            {
                var result = new List<Entry>(Math.Min(maxMessages, messages.Count) + metrics.Count);
                foreach (var kind in new List<LiveAudienceMetricKind>(metrics.Keys))
                {
                    if (result.Count >= maxMessages) break;
                    result.Add(metrics[kind]);
                    metrics.Remove(kind);
                }
                while (result.Count < maxMessages && messages.First != null)
                {
                    result.Add(messages.First.Value);
                    messages.RemoveFirst();
                }
                return result;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                messages.Clear();
                metrics.Clear();
            }
        }

        public static string GetSuperChatKey(LiveSuperChatMessage message)
        {
            if (message == null) return null;
            if (!string.IsNullOrWhiteSpace(message.Id)) return "id:" + message.Id;
            var user = message.UserName ?? string.Empty;
            var text = message.Message ?? string.Empty;
            return string.Format(CultureInfo.InvariantCulture, "fallback:{0}:{1}:{2}:{3}:{4}:{5}",
                message.StartTime.Ticks, message.Price, user.Length, user, text.Length, text);
        }
    }
}
