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
    static long MatchId;
    static DateTime StartAt = DateTime.UtcNow;
    static DateTime LastRecv = DateTime.UtcNow;

    public static int Main(string[] args)
    {
        if (args.Length > 0) Host = args[0];
        if (args.Length > 1) Port = int.Parse(args[1]);
        if (args.Length > 2) PickBrawler = int.Parse(args[2]);
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
            if ((DateTime.UtcNow - LastRecv).TotalSeconds > 45)
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
        s.WriteLong(0); // новый акк
        s.WriteString("");
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
                Log("LOGIN OK, жду дом...");
                break;
            case 24101: // OwnHomeData
                if (Step == 0)
                {
                    Step = 1;
                    Log("дома получены, встаю в ранкед соло (слот 14)");
                    SendMatchmake(14);
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
                Log("START LOADING — БОЙ СТАРТУЕТ, ТЕСТ ПРОЙДЕН");
                Environment.Exit(0);
                break;
        }
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
