// Тестовый бот ранкеда: проходит весь путь живого клиента
// login -> очередь ранкед соло -> бан -> пик -> тап гаджета -> бой.
// Использование: dotnet run --project IndusBrawl.Laser.Bot -- [host] [port]
// Выход: 0 = дошёл до StartLoading, 1 = упал/отвалился (смотри лог).
using System.Net.Sockets;
using IndusBrawl.Laser.Logic.Data;
using IndusBrawl.Laser.Logic.Data.Helper;
using IndusBrawl.Laser.Logic.Helper;
using IndusBrawl.Laser.Logic.Message;
using IndusBrawl.Laser.Titan.Cryptography;
using IndusBrawl.Laser.Titan.DataStream;
using IndusBrawl.Laser.Titan.Library;
using IndusBrawl.Laser.Titan.Library.Blake;
using IndusBrawl.Laser.Logic.Util;
using System.Net;
using System.Net.Http;
using static IndusBrawl.Laser.Titan.Library.TweetNaCl;

namespace IndusBrawl.Laser.Bot;

public static class Program
{
    static string Host = "127.0.0.1";
    static int Port = 9339;
    static TcpClient Tcp;
    static NetworkStream Net;
    static readonly List<byte> InBuf = new();

    // крипта сессии
    static byte[] Csk, Cpk, Spub, Shared, RNonce, SNonce, Secret;
    static PepperEncrypter Enc, Dec;
    static int PepperState = 2;

    static readonly byte[] Ssk = Convert.FromHexString(
        "7fba4ad5a0dac24719fd540fb96a42b0f72061bee3daa7dc16bbd5fc46c027f8");

    static int Step = 0; // 0 hello sent, 1 authed, 2 queued, 3 draft, 4 battle
    static int PickBrawler = 11;
    static string Mode = "ranked";
    static long LoginId = 0;
    static string LoginToken = "";
    static long GuestId = 0;
    static long MyAccountId = 0;
    static string AdminPass = "";
    static long UdpSessionId = -1;
    static System.Net.Sockets.UdpClient Udp;
    static int UdpTick = 0;
    static long MatchId;
    static DateTime StartAt = DateTime.UtcNow;
    static DateTime LastRecv = DateTime.UtcNow;

    public static int Main(string[] args)
    {
        if (args.Length > 0) Host = args[0];
        if (args.Length > 1) Port = int.Parse(args[1]);
        if (args.Length > 2)
            {
                if (args[2] == "team1" || args[2] == "ranked" || args[2] == "guest" || args[2] == "host1" || args[2] == "battle1") Mode = args[2];
                else PickBrawler = int.Parse(args[2]);
            }
            if (args.Length > 3 && Mode == "ranked") PickBrawler = int.Parse(args[3]);
            if (Mode == "guest") { LoginId = long.Parse(args[3]); LoginToken = args[4]; }
            if (Mode == "host1") GuestId = long.Parse(args[3]);
            if (Mode == "battle1" && args.Length > 3) AdminPass = args[3];
        try
        {
            Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log($"BOT FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static void Log(string s) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {s}");

    static void Run()
    {
        SelfTestCrypto();
        Tcp = new TcpClient();
        Tcp.Connect(Host, Port);
        Net = Tcp.GetStream();
        Net.ReadTimeout = 1000;
        Log($"connected to {Host}:{Port}");
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    Thread.Sleep(5000);
                    lock (Net) SendRaw(10108, Array.Empty<byte>(), 1);
                }
            }
            catch { }
        }).Start();

        // ключи
        Csk = new byte[32]; RandomBytes(Csk);
        Cpk = CryptoScalarmultBase(Csk);
        Spub = CryptoScalarmultBase(Ssk);

        // 10100 hello (сервер контент не читает, но декодит 4 int)
        var hello = new ByteStream(32);
        hello.WriteInt(0); hello.WriteInt(0); hello.WriteInt(53); hello.WriteInt(new Random().Next());
        SendRaw(10100, Fin(hello), 1);
        Log("sent 10100 hello");

        RNonce = new byte[24]; RandomBytes(RNonce);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            // в бою TCP тишина норма (всё идёт по UDP) — ждём до общего дедлайна
            if (UdpSessionId < 0 && (DateTime.UtcNow - LastRecv).TotalSeconds > 45)
                throw new Exception("таймаут: сервер молчит 45 сек");
            Thread.Sleep(50);
        }
        throw new Exception("таймаут 5 минут: до боя не дошли");
    }

    static void SelfTestCrypto()
    {
        byte[] key = new byte[32]; RandomBytes(key);
        byte[] nonce = new byte[24]; RandomBytes(nonce);
        var e = new PepperEncrypter(key, (byte[])nonce.Clone());
        var dd = new PepperEncrypter(key, (byte[])nonce.Clone());
        byte[] msg = System.Text.Encoding.UTF8.GetBytes("hello-ranked-test");
        byte[] enc = new byte[msg.Length + e.GetEncryptionOverhead()];
        e.Encrypt(msg, enc, msg.Length);
        byte[] dec = new byte[enc.Length - dd.GetEncryptionOverhead()];
        int r = dd.Decrypt(enc, dec, enc.Length);
        Log($"crypto selftest: r={r} back={System.Text.Encoding.UTF8.GetString(dec)}");
    }

    static byte[] Fin(ByteStream s)
    {
        var buf = s.GetByteArray();
        var out_ = new byte[s.GetOffset()];
        Buffer.BlockCopy(buf, 0, out_, 0, out_.Length);
        return out_;
    }

    static void SendRaw(int type, byte[] payload, int version)
    {
        byte[] frame;
        if (PepperState >= 5)
        {
            byte[] enc = new byte[payload.Length + Enc.GetEncryptionOverhead()];
            Enc.Encrypt(payload, enc, payload.Length);
            payload = enc;
        }
        frame = new byte[payload.Length + 7];
        frame[0] = (byte)(type >> 8); frame[1] = (byte)type;
        frame[2] = (byte)(payload.Length >> 16); frame[3] = (byte)(payload.Length >> 8); frame[4] = (byte)payload.Length;
        frame[5] = (byte)(version >> 8); frame[6] = (byte)version;
        Buffer.BlockCopy(payload, 0, frame, 7, payload.Length);
        Net.Write(frame, 0, frame.Length);
        Log($"sent type={type} frame_len={frame.Length} head={BitConverter.ToString(frame, 0, Math.Min(frame.Length, 20))}");
    }

    static byte[] Blake(params byte[][] parts)
    {
        var h = new Blake2BHasher();
        foreach (var p in parts) h.Update(p);
        return h.Finish();
    }

    static void Pump()
    {
        if (Net.DataAvailable)
        {
            var tmp = new byte[65536];
            int n = Net.Read(tmp, 0, tmp.Length);
            if (n == 0) throw new Exception("сервер закрыл соединение");
            InBuf.AddRange(tmp.Take(n));
            LastRecv = DateTime.UtcNow;
        }
        while (InBuf.Count >= 7)
        {
            int type = (InBuf[0] << 8) | InBuf[1];
            int len = (InBuf[2] << 16) | (InBuf[3] << 8) | InBuf[4];
            if (InBuf.Count < 7 + len) break;
            byte[] payload = InBuf.Skip(7).Take(len).ToArray();
            InBuf.RemoveRange(0, 7 + len);
            Console.WriteLine($"[FRAME] type={type} len={len}");
            OnMessage(type, payload);
        }
    }

    static void OnMessage(int type, byte[] payload)
    {
        if (type == 20100)
        {
            // plaintext ServerHello (24 байта токена), перец будет внутри 20104
            Log($"got 20100 hello ({payload.Length} bytes), sending login");
            SendLogin();
            return;
        }
        if (PepperState < 5 && type != 20100)
        {
            // первое сообщение после login = pepper: box(SNonce(24)+secret(32)+inner), nonce=Blake(RNonce||cpk||spub)
            byte[] nonce = Blake(RNonce, Cpk, Spub);
            byte[] s = CryptoBoxBeforenm(Spub, Csk);
            byte[] packet = CryptoBoxOpenAfternm(payload, nonce, s);
            SNonce = packet.Take(24).ToArray();
            Secret = packet.Skip(24).Take(32).ToArray();
            payload = packet.Skip(56).ToArray();
            Log($"pepper ok, loginOk inner={payload.Length} bytes, session encrypted");
            // перец идёт вне стрима — стримовые нонсы стартуют свежими
            Dec = new PepperEncrypter(Secret, (byte[])SNonce.Clone());
            Enc = new PepperEncrypter(Secret, (byte[])RNonce.Clone());
            PepperState = 5;
        }
        else if (PepperState >= 5)
        {
            byte[] dec = new byte[payload.Length - Dec.GetEncryptionOverhead()];
            if (Dec.Decrypt(payload, dec, payload.Length) != 0)
            {
                // DEBUG: ищем правильный шаг нонса
                for (int step = 1; step <= 6; step++)
                {
                    byte[] probe = (byte[])SNonce.Clone();
                    AdvNonce(probe, 2 + step * 2);
                    var pe = new PepperEncrypter(Secret, probe);
                    byte[] dd = new byte[payload.Length - pe.GetEncryptionOverhead()];
                    if (pe.Decrypt(payload, dd, payload.Length) == 0)
                        throw new Exception($"не смог расшифровать {type}, но шаг +{2 + step * 2} подошёл!");
                }
                Log($"fail payload len={payload.Length} head={BitConverter.ToString(payload, 0, Math.Min(payload.Length, 32))}");
                throw new Exception($"не смог расшифровать {type}");
            }
            payload = dec;
        }

    static void AdvNonce(byte[] n, int add)
    {
        int carry = add;
        for (int i = 0; i < n.Length && carry > 0; i++)
        {
            int v = n[i] + carry;
            n[i] = (byte)v;
            carry = v >> 8;
        }
    }
        var msg = MessageFactory.Instance.CreateMessageByType(type);
        string name = msg?.GetType().Name ?? "???";
        Log($"recv type={type} ({name}) len={payload.Length}");
        Handle(type, payload); // всегда по типу — фабрика знает не все серверные типы
        if (msg == null)
        {
            Log($"  raw: {BitConverter.ToString(payload, 0, Math.Min(payload.Length, 64))}");
            return;
        }
        try
        {
            msg.GetByteStream().SetByteArray(payload, payload.Length);
            msg.Decode();
        }
        catch (Exception ex)
        {
            Log($"  DECODE FAIL: {ex.Message}");
            return;
        }
        Handle(type, payload);
    }

    static void SendLogin()
    {
        // зеркало AuthenticationMessage.Decode
        var s = new ByteStream(512);
        s.WriteLong(LoginId);
        s.WriteString(LoginToken);
        s.WriteInt(53); s.WriteInt(0); s.WriteInt(7);
        s.WriteString("bot-sha");
        s.WriteString("bot-device");
        ByteStreamHelper.WriteDataReference(s, 0);
        s.WriteString("ru");
        s.WriteString("android-13");
        s.WriteBoolean(true);
        s.WriteStringReference("");
        s.WriteStringReference("");
        s.WriteBoolean(false);
        s.WriteString("");
        s.WriteInt(12345);
        s.WriteVInt(0);
        s.WriteStringReference("53.007");
        byte[] inner = Fin(s);

        // pepper-обёртка 10101: cpk(32) + box([pad24][RNonce][inner])
        byte[] box = new byte[24 + 24 + inner.Length];
        Buffer.BlockCopy(RNonce, 0, box, 24, 24);
        Buffer.BlockCopy(inner, 0, box, 48, inner.Length);
        byte[] sh = CryptoBoxBeforenm(Spub, Csk);
        byte[] enc = CryptoBoxAfternm(box, Blake(Cpk, Spub), sh);
        byte[] payload = new byte[32 + enc.Length];
        Buffer.BlockCopy(Cpk, 0, payload, 0, 32);
        Buffer.BlockCopy(enc, 0, payload, 32, enc.Length);
        SendRaw(10101, payload, 1);
        Log("sent 10101 login");
    }

    static void Handle(int type, byte[] payload)
    {
        switch (type)
        {
            case 20104: // auth ok
                try
                {
                    var bs = new ByteStream(payload, payload.Length);
                    bs.ReadLong();
                    MyAccountId = bs.ReadLong();
                    Log($"LOGIN OK acc={MyAccountId}, жду дом...");
                }
                catch { Log("LOGIN OK, жду дом..."); }
                break;
            case 24101: // OwnHomeData
                if (Step == 0)
                {
                    Step = 1;
                    if (Mode == "team1")
                    {
                        Log("дома получены, создаю дружескую комнату (тип 1)");
                        SendTeamCreate(1);
                    }
                    else if (Mode == "host1")
                    {
                        Log("дома получены, создаю комнату для друга");
                        SendTeamCreate(1);
                    }
                    else if (Mode == "guest")
                    {
                        Log("дома получены, жду инвайт...");
                    }
                    else if (Mode == "battle1")
                    {
                        Log("дома получены, встаю в обычный бой (слот 1)");
                        SendMatchmake(1);
                    }
                    else
                    {
                        Log("дома получены, встаю в ранкед соло (слот 14)");
                        SendMatchmake(14);
                    }
                }
                break;
            case 24124: // TeamMessage
                if (Mode == "team1" && Step == 1)
                {
                    Step = 2;
                    Log("комната создана, жму готов");
                    SendTeamReady(true);
                }
                else if (Mode == "host1" && Step == 1)
                {
                    Step = 2;
                    Log($"комната создана, зову друга {GuestId}");
                    SendInvite(GuestId);
                    Thread.Sleep(12000);
                    Log("жму готов");
                    SendTeamReady(true);
                }
                break;
            case 24589: // TeamInvitationMessage: VInt + Long teamId
                if (Mode == "guest" && Step == 1)
                {
                    try
                    {
                        var bs = new ByteStream(payload, payload.Length);
                        bs.ReadVInt();
                        long teamId = bs.ReadLong();
                        Step = 2;
                        Log($"инвайт в команду {teamId}, принимаю + готов");
                        SendInviteResponse(teamId);
                        SendTeamReady(true);
                    }
                    catch (Exception ex) { Log($"invite parse fail: {ex.Message}"); }
                }
                break;
            case 22150: Log("RANKED STARTED"); Step = 3; break;
            case 22151: Log("BAN STARTED, баню бойца 0"); SendBan(0); break;
            case 22154:
                Log($"PICK STARTED, пикаю бойца {PickBrawler}");
                SendPick(PickBrawler, 0);
                Thread.Sleep(1500);
                Log("тап гаджета 273 (pickType=2)");
                SendPick(273, 2);
                Thread.Sleep(1500);
                Log("тап пассивки 76 (pickType=2)");
                SendPick(76, 2);
                break;
            case 22156: Log("HERO PICKED echo"); break;
            case 22158: Log("FINAL PREP"); break;
            case 20559:
                if (Mode == "battle1" && UdpSessionId < 0)
                {
                    Log("START LOADING, подключаюсь по UDP и стою афк до конца боя");
                    UdpJoin();
                }
                else if (Mode != "battle1")
                {
                    Log("START LOADING — БОЙ СТАРТУЕТ, ТЕСТ ПРОЙДЕН");
                    Environment.Exit(0);
                }
                break;
            case 23456: // BattleEndMessage
                if (Mode == "battle1")
                {
                    Log("BATTLE END получен, ТЕСТ БОЯ ПРОЙДЕН");
                    Environment.Exit(0);
                }
                break;
        }
    }

    static void SendTeamCreate(int teamType)
    {
        var s = new ByteStream(32);
        s.WriteLong(0); s.WriteVInt(teamType); s.WriteVInt(1); s.WriteVInt(0);
        SendRaw(12541, Fin(s), 1);
    }

    static void SendInvite(long guestId)
    {
        var s = new ByteStream(32);
        ByteStreamHelper.EncodeLogicLong(s, guestId);
        s.WriteVInt(0);
        SendRaw(14365, Fin(s), 1);
    }

    static void SendInviteResponse(long teamId)
    {
        var s = new ByteStream(32);
        s.WriteVInt(1);
        s.WriteLong(teamId);
        s.WriteBoolean(false);
        SendRaw(14479, Fin(s), 1);
    }

    static void SendTeamReady(bool ready)
    {
        var s = new ByteStream(8);
        s.WriteBoolean(ready);
        SendRaw(14355, Fin(s), 1);
    }

    static HttpClient AdminHttp;
    static string AdminGet(string path)
    {
        if (AdminHttp == null)
        {
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            AdminHttp = new HttpClient(handler);
            string loginJson = "{\"login\":\"admin\",\"password\":\"" + AdminPass + "\"}";
            var lr = AdminHttp.PostAsync("http://127.0.0.1:8086/api/login",
                new StringContent(loginJson, System.Text.Encoding.UTF8, "application/json")).Result;
            Log("admin login: " + ((int)lr.StatusCode));
        }
        return AdminHttp.GetStringAsync("http://127.0.0.1:8086" + path).Result;
    }

    static int MoveX = 300, MoveY = 0;
    static string MyTag = "";

    static void UdpJoin()
    {
        try
        {
            MyTag = LogicLongCodeGenerator.ToCode(MyAccountId);
            string js = AdminGet("/api/test/session?tag=" + Uri.EscapeDataString(MyTag));
            Log("session info: " + js);
            string marker = "\"udpSessionId\":";
            long sid = long.Parse(js.Split(marker)[1].Split('}')[0].Trim().TrimEnd(','));
            UdpSessionId = sid;
            Udp = new System.Net.Sockets.UdpClient();
            Udp.Connect(Host, 1337);
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        UdpSendInput();
                        Thread.Sleep(200);
                    }
                }
                catch { }
            }).Start();
            Log("udp join session=" + sid + ", иду +X, проверяю позицию...");
            new Thread(MoveTest).Start();
        }
        catch (Exception ex) { Log("udp join fail: " + ex.Message); }
    }

    static void MoveTest()
    {
        try
        {
            Thread.Sleep(3000);
            string a = AdminGet("/api/test/battle?tag=" + Uri.EscapeDataString(MyTag));
            Log("pos t0: " + a);
            Thread.Sleep(8000);
            string b = AdminGet("/api/test/battle?tag=" + Uri.EscapeDataString(MyTag));
            Log("pos t1: " + b);
            if (a != b) Log("ДВИЖЕНИЕ ЕСТЬ: позиция изменилась");
            else Log("ДВИЖЕНИЯ НЕТ: позиция та же");
        }
        catch (Exception ex) { Log("movetest fail: " + ex.Message); }
    }

    static int MoveTX = 6000, MoveTY = 9750;

    static void UdpSendInput(int type = 2, int x = 6000, int y = 9750)
    {
        var bits = new IndusBrawl.Laser.Titan.DataStream.BitStream(128);
        bits.WritePositiveInt(UdpTick++, 14);
        bits.WritePositiveInt(0, 10);
        bits.WritePositiveInt(0, 13);
        bits.WritePositiveInt(0, 10);
        bits.WritePositiveInt(0, 10);
        bits.WritePositiveInt(0, 10);
        bits.WritePositiveInt(1, 5); // count=1
        bits.WritePositiveInt(0, 15); // Index
        bits.WritePositiveInt(type, 5); // Type: 2 = движение
        bits.WriteInt(x, 15);
        bits.WriteInt(y, 15);
        bits.WriteBoolean(false);
        bits.WriteBoolean(false); // AutoAttack
        bits.WriteBoolean(false);
        byte[] raw = bits.GetByteArray();
        byte[] body = new byte[17]; // 72 + 15+5+15+15+3 = 125 бит
        Buffer.BlockCopy(raw, 0, body, 0, Math.Min(body.Length, raw.Length));
        var bs = new ByteStream(48);
        bs.WriteLong(UdpSessionId);
        bs.WriteShort((short)0);
        bs.WriteVInt(10555);
        bs.WriteVInt(body.Length);
        byte[] head = Fin(bs);
        byte[] pkt = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, pkt, 0, head.Length);
        Buffer.BlockCopy(body, 0, pkt, head.Length, body.Length);
        lock (Udp) Udp.Send(pkt, pkt.Length);
    }

    static void SendMatchmake(int slot)
    {
        var s = new ByteStream(32);
        s.WriteVInt(0);
        ByteStreamHelper.WriteDataReference(s, 0);
        s.WriteVInt(slot);
        s.WriteVInt(0);
        s.WriteVInt(0); // Unk3
        SendRaw(18977, Fin(s), 1);
        Step = 2;
    }

    static void SendBan(int brawler)
    {
        var s = new ByteStream(32);
        s.WriteVInt(0); s.WriteVInt(brawler); s.WriteVInt(0);
        SendRaw(12152, Fin(s), 1);
    }

    static void SendPick(int brawler, int pickType)
    {
        var s = new ByteStream(32);
        s.WriteVInt(0); s.WriteVInt(0); s.WriteVInt(brawler); s.WriteVInt(pickType);
        SendRaw(12155, Fin(s), 1);
    }
}
