using System.Collections.Concurrent;

namespace NSYazilim.Web.Services
{
    public class LiveChatMemoryStore
    {
        private readonly ConcurrentDictionary<string, LiveChatSession> _sessions = new();

        public LiveChatSession GetOrCreate(string sessionId, string? contact = null)
        {
            sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId.Trim();
            var session = _sessions.GetOrAdd(sessionId, id => new LiveChatSession
            {
                SessionId = id,
                Contact = string.IsNullOrWhiteSpace(contact) ? "Ziyaretçi" : contact.Trim(),
                StartedAt = DateTime.Now,
                LastMessageAt = DateTime.Now
            });

            if (!string.IsNullOrWhiteSpace(contact))
                session.Contact = contact.Trim();

            return session;
        }

        public LiveChatMessage AddMessage(string sessionId, string sender, string text, string? contact = null)
        {
            var session = GetOrCreate(sessionId, contact);
            var message = new LiveChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = session.SessionId,
                Sender = sender,
                Text = (text ?? string.Empty).Trim(),
                CreatedAt = DateTime.Now
            };

            session.Messages.Add(message);
            session.LastMessageAt = message.CreatedAt;
            session.LastMessageText = message.Text;
            session.UnreadForAdmin = sender == "Müşteri" ? session.UnreadForAdmin + 1 : session.UnreadForAdmin;
            return message;
        }

        public void MarkAdminRead(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
                session.UnreadForAdmin = 0;
        }

        public bool RemoveSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            return _sessions.TryRemove(sessionId.Trim(), out _);
        }

        public void ClearSessions()
        {
            _sessions.Clear();
        }

        public IReadOnlyCollection<LiveChatSession> Sessions()
        {
            return _sessions.Values
                .OrderByDescending(x => x.LastMessageAt)
                .ToList();
        }
    }

    public class LiveChatSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string Contact { get; set; } = "Ziyaretçi";
        public DateTime StartedAt { get; set; }
        public DateTime LastMessageAt { get; set; }
        public string LastMessageText { get; set; } = string.Empty;
        public int UnreadForAdmin { get; set; }
        public List<LiveChatMessage> Messages { get; set; } = new();
    }

    public class LiveChatMessage
    {
        public string Id { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
