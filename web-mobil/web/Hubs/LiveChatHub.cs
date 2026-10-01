using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Hubs
{
    public class LiveChatHub : Hub
    {
        private readonly LiveChatMemoryStore _store;

        public LiveChatHub(LiveChatMemoryStore store)
        {
            _store = store;
        }

        public async Task CustomerJoin(string sessionId, string contact)
        {
            var session = _store.GetOrCreate(sessionId, contact);
            await Groups.AddToGroupAsync(Context.ConnectionId, $"chat-{session.SessionId}");
            await Clients.Caller.SendAsync("SessionReady", new
            {
                sessionId = session.SessionId,
                contact = session.Contact,
                messages = session.Messages.Select(ToDto)
            });
        }

        public async Task CustomerSend(string sessionId, string contact, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var chatMessage = _store.AddMessage(sessionId, "Müşteri", message, contact);
            await Groups.AddToGroupAsync(Context.ConnectionId, $"chat-{chatMessage.SessionId}");
            var dto = ToDto(chatMessage);

            await Clients.Group($"chat-{chatMessage.SessionId}").SendAsync("ReceiveCustomerMessage", dto);
            await Clients.Group("admins").SendAsync("ReceiveAdminMessage", dto, new
            {
                sessionId = chatMessage.SessionId,
                contact = string.IsNullOrWhiteSpace(contact) ? "Ziyaretçi" : contact.Trim(),
                lastMessageAt = chatMessage.CreatedAt.ToString("HH:mm"),
                lastMessageText = chatMessage.Text
            });
        }

        [Authorize(Roles = "Admin")]
        public async Task AdminJoin()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "admins");
            await Clients.Caller.SendAsync("AdminSessions", _store.Sessions().Select(ToSessionDto));
        }

        [Authorize(Roles = "Admin")]
        public async Task AdminOpenSession(string sessionId)
        {
            _store.MarkAdminRead(sessionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, $"chat-{sessionId}");
            var session = _store.GetOrCreate(sessionId);
            await Clients.Caller.SendAsync("AdminSessionOpened", new
            {
                sessionId = session.SessionId,
                contact = session.Contact,
                messages = session.Messages.Select(ToDto)
            });
        }

        [Authorize(Roles = "Admin")]
        public async Task AdminSend(string sessionId, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var chatMessage = _store.AddMessage(sessionId, "Admin", message);
            var dto = ToDto(chatMessage);
            await Clients.Group($"chat-{sessionId}").SendAsync("ReceiveAdminReply", dto);
            await Clients.Group("admins").SendAsync("ReceiveAdminReplyForPanel", dto);
        }

        [Authorize(Roles = "Admin")]
        public async Task AdminDeleteSession(string sessionId)
        {
            _store.RemoveSession(sessionId);
            await Clients.Group("admins").SendAsync("AdminSessions", _store.Sessions().Select(ToSessionDto));
        }

        [Authorize(Roles = "Admin")]
        public async Task AdminClearSessions()
        {
            _store.ClearSessions();
            await Clients.Group("admins").SendAsync("AdminSessions", _store.Sessions().Select(ToSessionDto));
        }

        private static object ToSessionDto(LiveChatSession session)
        {
            return new
            {
                sessionId = session.SessionId,
                contact = session.Contact,
                startedAt = session.StartedAt.ToString("dd.MM.yyyy HH:mm"),
                lastMessageAt = session.LastMessageAt.ToString("HH:mm"),
                lastMessageText = session.LastMessageText,
                unreadForAdmin = session.UnreadForAdmin,
                messages = session.Messages.Select(ToDto)
            };
        }

        private static object ToDto(LiveChatMessage message)
        {
            return new
            {
                id = message.Id,
                sessionId = message.SessionId,
                sender = message.Sender,
                text = message.Text,
                createdAt = message.CreatedAt.ToString("HH:mm")
            };
        }
    }
}
