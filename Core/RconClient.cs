using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SandstormModLauncher.Core
{
    public enum RconError { Unreachable, AuthFailed, Timeout, Closed, Protocol }

    public sealed class RconException : Exception
    {
        public RconError Kind { get; }
        public RconException(RconError kind, string message) : base(message) { Kind = kind; }
    }

    /// <summary>
    /// Source RCON (the protocol Insurgency: Sandstorm's own RCON server speaks) over a local TCP connection.
    /// Every read and write has a timeout, and each command is followed by a marker command, so a long reply
    /// that the game splits over several packets is always read to its end and never mixed with the next one.
    /// </summary>
    public sealed class RconClient : IDisposable
    {
        private const int Auth = 3, AuthResponse = 2, Exec = 2, ResponseValue = 0;
        private const int MaxPacket = 1024 * 1024;
        public const string MarkerCommand = "help sml-end-of-reply";

        private readonly string host;
        private readonly int port;
        private readonly string password;
        private TcpClient tcp;
        private NetworkStream stream;
        private int nextId = 100;

        public RconClient(string host, int port, string password)
        {
            this.host = host;
            this.port = port;
            this.password = password ?? "";
        }

        public bool Connected => stream != null && tcp != null && tcp.Connected;

        /// <summary>Connects and logs in. Throws RconException (Unreachable, AuthFailed, Timeout, Closed).</summary>
        public void Connect(int connectTimeoutMs, int authTimeoutMs)
        {
            Close();
            tcp = new TcpClient { NoDelay = true };
            try
            {
                var ar = tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(connectTimeoutMs)) throw new RconException(RconError.Unreachable, "nothing is listening on " + host + ":" + port);
                tcp.EndConnect(ar);
            }
            catch (SocketException ex) { Close(); throw new RconException(RconError.Unreachable, ex.Message); }
            catch (RconException) { Close(); throw; }
            stream = tcp.GetStream();
            int id = nextId++;
            Write(id, Auth, password);
            // Some servers send an empty RESPONSE_VALUE before the AUTH_RESPONSE.
            var until = DateTime.UtcNow.AddMilliseconds(authTimeoutMs);
            while (true)
            {
                var p = Read(Remaining(until));
                if (p.Type != AuthResponse) continue;
                if (p.Id == -1) { Close(); throw new RconException(RconError.AuthFailed, "the game did not accept the RCON password"); }
                if (p.Id == id) return;
            }
        }

        /// <summary>
        /// Runs one command and returns the game's whole reply. The marker command after it tells where the
        /// reply ends. Throws RconException on a timeout or a closed connection.
        /// </summary>
        public string Send(string command, int timeoutMs)
        {
            if (!Connected) throw new RconException(RconError.Closed, "not connected");
            int id = nextId++, marker = nextId++;
            Write(id, Exec, command ?? "");
            Write(marker, Exec, MarkerCommand);
            var reply = new StringBuilder();
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var p = Read(Remaining(until));
                if (p.Id == marker) return reply.ToString();
                if (p.Id == id) reply.Append(p.Body);
                // Anything else (a late reply to an earlier, timed-out command) is skipped.
            }
        }

        private int Remaining(DateTime until)
        {
            int ms = (int)(until - DateTime.UtcNow).TotalMilliseconds;
            if (ms <= 0) { Close(); throw new RconException(RconError.Timeout, "the game did not answer in time"); }
            return ms;
        }

        private void Write(int id, int type, string body)
        {
            byte[] text = Encoding.UTF8.GetBytes(body);
            var packet = new byte[4 + 4 + 4 + text.Length + 2];
            BitConverter.GetBytes(4 + 4 + text.Length + 2).CopyTo(packet, 0);
            BitConverter.GetBytes(id).CopyTo(packet, 4);
            BitConverter.GetBytes(type).CopyTo(packet, 8);
            text.CopyTo(packet, 12);
            try { stream.Write(packet, 0, packet.Length); }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                Close();
                throw new RconException(RconError.Closed, "the connection closed: " + ex.Message);
            }
        }

        private struct Packet { public int Id, Type; public string Body; }

        private Packet Read(int timeoutMs)
        {
            byte[] head = ReadExact(4, timeoutMs);
            int size = BitConverter.ToInt32(head, 0);
            if (size < 10 || size > MaxPacket) { Close(); throw new RconException(RconError.Protocol, "unexpected packet size " + size); }
            byte[] rest = ReadExact(size, timeoutMs);
            int end = size - 2;
            while (end > 8 && rest[end - 1] == 0) end--;
            return new Packet
            {
                Id = BitConverter.ToInt32(rest, 0),
                Type = BitConverter.ToInt32(rest, 4),
                Body = Encoding.UTF8.GetString(rest, 8, Math.Max(0, end - 8))
            };
        }

        private byte[] ReadExact(int count, int timeoutMs)
        {
            var buffer = new byte[count];
            int got = 0;
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (got < count)
            {
                int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) throw new RconException(RconError.Timeout, "the game did not answer in time");
                try
                {
                    tcp.ReceiveTimeout = Math.Max(1, left);
                    int n = stream.Read(buffer, got, count - got);
                    if (n <= 0) { Close(); throw new RconException(RconError.Closed, "the game closed the connection"); }
                    got += n;
                }
                catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut)
                {
                    // A socket that timed out mid-read is not reused: a late reply could still arrive on it.
                    Close();
                    throw new RconException(RconError.Timeout, "the game did not answer in time");
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
                {
                    Close();
                    throw new RconException(RconError.Closed, "the connection closed: " + ex.Message);
                }
            }
            return buffer;
        }

        public void Close()
        {
            try { stream?.Dispose(); } catch { }
            try { tcp?.Close(); } catch { }
            stream = null;
            tcp = null;
        }

        public void Dispose() => Close();
    }
}
