// Telegram/GemMarket.cs
// покупка гемов за Telegram Stars (валюта XTR). курс: 1 звезда = 50 гемов.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IndusBrawl.Laser.Logic.Message.Account.Auth;
using IndusBrawl.Laser.Logic.Util;
using IndusBrawl.Laser.Server.Database;
using IndusBrawl.Laser.Server.Database.Models;
using IndusBrawl.Laser.Server.Networking.Session;
using IndusBrawl.Laser.Server.Telegram;
using Newtonsoft.Json;

namespace IndusBrawl.Laser.Server.Bot
{
    public class GemMarket
    {
        public class Pack
        {
            public string Key;
            public string Title;
            public int Gems;
            public int Stars;
        }

        public class PaymentRecord
        {
            public string ChargeId { get; set; }
            public long TelegramUserId { get; set; }
            public string AccountTag { get; set; }
            public int Gems { get; set; }
            public int Stars { get; set; }
            public DateTime PaidAt { get; set; }
        }

        // курс 1 звезда = 50 гемов
        public const int GemsPerStar = 50;

        public static readonly Pack[] Packs =
        {
            new Pack { Key = "s",  Title = "500 гемов",   Gems = 500,  Stars = 10 },
            new Pack { Key = "m",  Title = "1250 гемов",  Gems = 1250, Stars = 25 },
            new Pack { Key = "l",  Title = "3000 гемов",  Gems = 3000, Stars = 60 },
            new Pack { Key = "xl", Title = "5000 гемов",  Gems = 5000, Stars = 100 },
        };

        private const string PAYMENTS_PATH = "gem_payments.json";
        private const string SUPPORT_CONTACT = "@CoachBrawl";

        private readonly TelegramClient _client;
        private readonly TelegramLinkManager _linkManager;
        private readonly HashSet<long> _adminIds;
        private readonly object _lock = new object();
        private List<PaymentRecord> _payments = new List<PaymentRecord>();

        public GemMarket(TelegramClient client, TelegramLinkManager linkManager, HashSet<long> adminIds)
        {
            _client = client;
            _linkManager = linkManager;
            _adminIds = adminIds;
            LoadPayments();
        }

        public async Task ShowMarketAsync(long chatId, long userId)
        {
            if (!_linkManager.TryGetAccountId(userId, out string tag))
            {
                await SendAsync(chatId,
                    "💎 <b>гемы за звёзды</b>\n\n" +
                    "сначала привяжи игровой акк: просто отправь боту свой тэг (например: #2pp).",
                    new { inline_keyboard = new object[] { new object[] { new { text = "← назад", callback_data = "menu_main" } } } });
                return;
            }

            int balance = 0;
            Account account = LoadAccount(tag);
            if (account?.Avatar != null) balance = account.Avatar.Diamonds;

            string text =
                "💎 <b>гемы за звёзды</b>\n" +
                "▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬\n\n" +
                "курс: 1 ⭐ = 50 💎\n" +
                "гемы падают сразу на акк, перезаходить не надо.\n\n" +
                $"👤 акк: <code>{Escape(tag)}</code>\n" +
                $"💎 сейчас: {balance}\n\n" +
                "<b>выбери пак:</b>";

            var rows = Packs
                .Select(p => new object[] { new { text = $"{p.Title} — {p.Stars} ⭐", callback_data = "gem_buy_" + p.Key } })
                .ToList();
            rows.Add(new object[] { new { text = "← назад", callback_data = "menu_main" } });

            await SendAsync(chatId, text, new { inline_keyboard = rows.ToArray() });
        }

        public async Task SendInvoiceAsync(long chatId, long userId, string packKey)
        {
            Pack pack = Packs.FirstOrDefault(p => p.Key == packKey);
            if (pack == null) return;

            if (!_linkManager.TryGetAccountId(userId, out string tag))
            {
                await ShowMarketAsync(chatId, userId);
                return;
            }

            var (ok, body) = await _client.CallAsync("sendInvoice", new
            {
                chat_id = chatId,
                title = $"гемы coachbrawl — {pack.Title}",
                description = $"{pack.Gems} гемов на акк {tag}. курс 1 звезда = {GemsPerStar} гемов.",
                payload = BuildPayload(pack, tag),
                provider_token = "",
                currency = "XTR",
                prices = new object[] { new { label = pack.Title, amount = pack.Stars } }
            });

            if (!ok)
            {
                Console.WriteLine($"[GemMarket] sendInvoice error: {body}");
                await SendAsync(chatId, "❌ не получилось создать счёт. попробуй чуть позже.", null);
            }
        }

        public async Task HandlePreCheckoutAsync(PreCheckoutQuery query)
        {
            string error = null;

            if (!TryParsePayload(query.invoice_payload, out Pack pack, out string tag))
                error = "счёт устарел. открой гем-маркет заново.";
            else if (query.currency != "XTR" || query.total_amount != pack.Stars)
                error = "цена изменилась. открой гем-маркет заново.";
            else if (LoadAccount(tag) == null)
                error = "игровой акк не найден.";

            var (ok, body) = await _client.CallAsync("answerPreCheckoutQuery", error == null
                ? new { pre_checkout_query_id = query.id, ok = true, error_message = (string)null }
                : new { pre_checkout_query_id = query.id, ok = false, error_message = error });

            if (!ok) Console.WriteLine($"[GemMarket] answerPreCheckoutQuery error: {body}");
        }

        public async Task HandleSuccessfulPaymentAsync(IndusBrawl.Laser.Server.Telegram.Message message)
        {
            var payment = message.successful_payment;
            long chatId = message.chat.id;
            long userId = message.from?.id ?? chatId;

            lock (_lock)
            {
                if (_payments.Any(p => p.ChargeId == payment.telegram_payment_charge_id)) return;
            }

            Account account = null;
            Pack pack = null;
            string tag = null;
            if (TryParsePayload(payment.invoice_payload, out pack, out tag))
            {
                account = LoadAccount(tag);
            }

            if (account?.Avatar == null)
            {
                Console.WriteLine($"[GemMarket] платёж {payment.telegram_payment_charge_id}: акк не найден, возврат");
                var (refunded, body) = await _client.CallAsync("refundStarPayment", new
                {
                    user_id = userId,
                    telegram_payment_charge_id = payment.telegram_payment_charge_id
                });
                if (!refunded) Console.WriteLine($"[GemMarket] refundStarPayment error: {body}");

                await SendAsync(chatId, refunded
                    ? "❌ акк не найден, гемы не выдал. звёзды вернул."
                    : $"❌ акк не найден, гемы не выдал. напиши {SUPPORT_CONTACT} для возврата звёзд.", null);
                return;
            }

            account.Avatar.AddDiamonds(pack.Gems);
            Accounts.Save(account);

            // если игрок онлайн — кикаем, чтоб гемы подтянулись
            long accountId = account.AccountId;
            if (Sessions.IsSessionActive(accountId))
            {
                var session = Sessions.GetSession(accountId);
                session.GameListener.SendTCPMessage(new AuthenticationFailedMessage()
                {
                    Message = $"начислил +{pack.Gems} гемов! перезайди в игру."
                });
                Sessions.Remove(accountId);
            }

            var record = new PaymentRecord
            {
                ChargeId = payment.telegram_payment_charge_id,
                TelegramUserId = userId,
                AccountTag = tag,
                Gems = pack.Gems,
                Stars = payment.total_amount,
                PaidAt = DateTime.UtcNow
            };
            lock (_lock)
            {
                _payments.Add(record);
                SavePayments();
            }

            Console.WriteLine($"[GemMarket] +{pack.Gems} гемов выдан {tag} (тг {userId}) за {payment.total_amount} xtr");

            await SendAsync(chatId,
                "✅ <b>гемы уже на акке!</b>\n\n" +
                $"👤 акк: <code>{Escape(tag)}</code>\n" +
                $"💎 пак: +{pack.Gems} гемов за {payment.total_amount} ⭐\n" +
                $"💎 баланс теперь: {account.Avatar.Diamonds}\n\n" +
                "если был в игре — перезайди, чтоб увидеть.\n\n" +
                "спасибки за поддержку coachbrawl!",
                new { inline_keyboard = new object[] { new object[] { new { text = "← в меню", callback_data = "menu_main" } } } });

            foreach (long adminId in _adminIds)
            {
                if (adminId == userId) continue;
                await SendAsync(adminId, $"💎 покупка гемов: {Escape(tag)}, +{pack.Gems} за {payment.total_amount} ⭐ (тг {userId})", null);
            }
        }

        public static string BuildPayload(Pack pack, string tag) => $"gem|{pack.Key}|{tag}";

        public static bool TryParsePayload(string payload, out Pack pack, out string tag)
        {
            pack = null;
            tag = null;
            if (string.IsNullOrEmpty(payload)) return false;

            string[] parts = payload.Split('|');
            if (parts.Length != 3 || parts[0] != "gem") return false;

            string key = parts[1];
            pack = Packs.FirstOrDefault(p => p.Key == key);
            tag = parts[2];
            return pack != null && !string.IsNullOrEmpty(tag);
        }

        private static Account LoadAccount(string tag)
        {
            try
            {
                return Accounts.Load(LogicLongCodeGenerator.ToId(tag));
            }
            catch
            {
                return null;
            }
        }

        private async Task SendAsync(long chatId, string html, object keyboard)
        {
            try
            {
                var (ok, body) = await _client.CallAsync("sendMessage", keyboard != null
                    ? new { chat_id = chatId, text = html, parse_mode = "HTML", reply_markup = keyboard }
                    : (object)new { chat_id = chatId, text = html, parse_mode = "HTML" });
                if (!ok) Console.WriteLine($"[GemMarket] sendMessage error: {body}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GemMarket] sendMessage exception: {ex.Message}");
            }
        }

        private static string Escape(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private void LoadPayments()
        {
            try
            {
                if (File.Exists(PAYMENTS_PATH))
                {
                    _payments = JsonConvert.DeserializeObject<List<PaymentRecord>>(File.ReadAllText(PAYMENTS_PATH)) ?? new List<PaymentRecord>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GemMarket] ошибка загрузки {PAYMENTS_PATH}: {ex.Message}");
            }
        }

        private void SavePayments()
        {
            try
            {
                File.WriteAllText(PAYMENTS_PATH, JsonConvert.SerializeObject(_payments, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GemMarket] ошибка сохранения {PAYMENTS_PATH}: {ex.Message}");
            }
        }
    }
}
