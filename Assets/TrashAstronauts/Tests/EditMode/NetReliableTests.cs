using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// ReliableChannel and MemoryTransport: exactly-once, in-order delivery under seeded loss, reordering and
    /// duplication, across the 16-bit sequence wrap, inside the datagram cap and the backlog limits.
    /// </summary>
    public class NetReliableTests
    {
        /// <summary>One end of a link: a channel, a transport, and the smallest possible datagram framing around them.</summary>
        sealed class End
        {
            public readonly ReliableChannel Channel;
            public readonly MemoryTransport Transport;
            public NetEndpoint Peer;
            public readonly List<int> Received = new List<int>();
            public int Corrupt;
            public int DatagramsSent;
            public int LargestDatagram;
            readonly byte[] m_Send = new byte[NetProtocol.MaxDatagram];
            readonly byte[] m_Receive = new byte[NetProtocol.MaxDatagram + 64];
            readonly byte[] m_Payload = new byte[ReliableChannel.MaxMessageBytes];

            public End(MemoryTransportHub hub, ushort firstSequence)
            {
                Channel = new ReliableChannel(firstSequence);
                Transport = hub.Create();
            }

            /// <summary>Queues message number <paramref name="index"/>: its payload size and bytes are derived from the index so the receiver can check them.</summary>
            public bool Enqueue(int index, int size = -1)
            {
                if (size < 0) size = 4 + (index * 37) % 60;
                var w = new NetWriter(m_Payload);
                w.I32(index);
                for (int i = 4; i < size; i++) w.U8((byte)(index * 7 + i));
                return Channel.Enqueue((byte)(NetMsg.FirstReliable + index % 100), m_Payload, 0, size);
            }

            public void SendDatagram(double now, bool force = false)
            {
                var w = new NetWriter(m_Send, 0, NetProtocol.MaxDatagram);
                var header = new DatagramHeader { protocol = 1, connId = 1, seq = Channel.NextSendSequence(), timeMs = DatagramHeader.ToMs(now) };
                Channel.GetAck(out header.ack, out header.ackBits);
                header.Write(ref w);
                Channel.WriteDue(ref w, header.seq, now, force);
                Assert.IsFalse(w.Overflow);
                Assert.LessOrEqual(w.Position, NetProtocol.MaxDatagram);
                if (w.Position > LargestDatagram) LargestDatagram = w.Position;
                Transport.Send(Peer, m_Send, w.Position);
                DatagramsSent++;
            }

            public void ReceiveAll()
            {
                while (Transport.Receive(out _, m_Receive, out int length))
                {
                    var r = new NetReader(m_Receive, 0, length);
                    if (!DatagramHeader.Read(ref r, out DatagramHeader header)) { Corrupt++; continue; }
                    if (!Channel.OnDatagramReceived(header.seq)) continue;
                    Channel.OnAck(header.ack, header.ackBits);
                    while (!r.AtEnd)
                    {
                        byte type = r.U8();
                        ushort relSeq = r.U16();
                        int size = r.U16();
                        if (r.Failed || size > r.Remaining) { Corrupt++; break; }
                        Channel.Accept(relSeq, type, m_Receive, r.Position, size);
                        r.Skip(size);
                    }
                    while (Channel.TryDeliver(out byte type, out byte[] data, out int size))
                    {
                        var mr = new NetReader(data, 0, size);
                        int index = mr.I32();
                        if (type != (byte)(NetMsg.FirstReliable + index % 100)) Corrupt++;
                        for (int i = 4; i < size; i++) if (mr.U8() != (byte)(index * 7 + i)) { Corrupt++; break; }
                        if (mr.Failed) Corrupt++;
                        Received.Add(index);
                    }
                }
            }
        }

        sealed class Link
        {
            public readonly MemoryTransportHub Hub;
            public readonly End A, B;
            public double Now;

            public Link(int seed, ushort firstSequence = 0)
            {
                Hub = new MemoryTransportHub(seed);
                A = new End(Hub, firstSequence);
                B = new End(Hub, firstSequence);
                A.Peer = B.Transport.Local;
                B.Peer = A.Transport.Local;
            }

            /// <summary>Advances 10 ms at a time; both ends receive every step and send a datagram every 50 ms.</summary>
            public void Run(double seconds, Action<int> eachStep = null)
            {
                int steps = (int)Math.Round(seconds / 0.01);
                for (int i = 0; i < steps; i++)
                {
                    Hub.Advance(0.01);
                    Now += 0.01;
                    A.ReceiveAll();
                    B.ReceiveAll();
                    eachStep?.Invoke(i);
                    if (i % 5 == 0)
                    {
                        A.SendDatagram(Now);
                        B.SendDatagram(Now);
                    }
                }
            }
        }

        static void AssertInOrderExactlyOnce(List<int> received, int count, string who)
        {
            Assert.AreEqual(count, received.Count, who + ": message count");
            for (int i = 0; i < count; i++)
                if (received[i] != i) Assert.Fail(who + ": position " + i + " holds message " + received[i]);
        }

        [Test]
        public void CleanLink_DeliversEverythingInOrder_WithoutResends()
        {
            var link = new Link(1);
            for (int i = 0; i < 50; i++) Assert.IsTrue(link.A.Enqueue(i));
            link.Run(1.0);
            AssertInOrderExactlyOnce(link.B.Received, 50, "B");
            Assert.AreEqual(0, link.A.Channel.Backlog, "everything acked");
            Assert.AreEqual(0, link.A.Channel.ResentMessages, "no resend on a clean, fast link");
            Assert.AreEqual(0, link.B.Corrupt);
        }

        [Test]
        public void ThirtyPercentLoss_Reorder_AndDuplication_StillDeliverExactlyOnceInOrder_BothWays()
        {
            foreach (int seed in new[] { 7, 99, 2024 })
            {
                var link = new Link(seed);
                link.Hub.Loss = 0.30;
                link.Hub.Reorder = 0.30;
                link.Hub.ReorderDelay = 0.12;
                link.Hub.Duplicate = 0.20;
                link.Hub.Latency = 0.03;
                link.Hub.Jitter = 0.03;
                const int count = 2000;
                int nextA = 0, nextB = 0;
                var random = new Random(seed);
                link.Run(120.0, step =>
                {
                    // bursts of new messages from both ends while the link misbehaves
                    for (int n = random.Next(0, 3); n > 0 && nextA < count; n--) Assert.IsTrue(link.A.Enqueue(nextA++), "A backlog overflowed");
                    for (int n = random.Next(0, 3); n > 0 && nextB < count; n--) Assert.IsTrue(link.B.Enqueue(nextB++), "B backlog overflowed");
                });
                Assert.AreEqual(count, nextA);
                AssertInOrderExactlyOnce(link.B.Received, count, "seed " + seed + " A->B");
                AssertInOrderExactlyOnce(link.A.Received, count, "seed " + seed + " B->A");
                Assert.AreEqual(0, link.A.Corrupt + link.B.Corrupt, "payload bytes intact");
                Assert.AreEqual(0, link.A.Channel.Backlog);
                Assert.AreEqual(0, link.B.Channel.Backlog);
                Assert.Greater(link.Hub.LostCount, 500, "the hub really dropped datagrams");
                Assert.Greater(link.Hub.DuplicatedCount, 300, "the hub really duplicated datagrams");
                Assert.Greater(link.A.Channel.ResentMessages, 100, "losses were repaired by resends");
                Assert.Greater(link.B.Channel.DuplicateDatagrams, 100, "duplicates were seen and discarded");
                Assert.LessOrEqual(link.Hub.LargestDatagram, NetProtocol.MaxDatagram);
                Assert.IsFalse(link.A.Channel.Overflowed);
            }
        }

        [Test]
        public void SequenceNumbers_WrapAround16Bits_UnderLossReorderAndDuplication()
        {
            // Start 40 below the wrap so both the datagram sequence and relSeq cross 65535 -> 0 early, then
            // send enough to wrap relSeq completely a second time.
            var link = new Link(5, 65496);
            link.Hub.Loss = 0.30;
            link.Hub.Reorder = 0.30;
            link.Hub.Duplicate = 0.15;
            link.Hub.Latency = 0.02;
            const int count = 3000;
            int next = 0;
            link.Run(150.0, step =>
            {
                for (int n = 0; n < 2 && next < count; n++) Assert.IsTrue(link.A.Enqueue(next++));
            });
            AssertInOrderExactlyOnce(link.B.Received, count, "across the wrap");
            Assert.AreEqual(0, link.B.Corrupt);
            Assert.AreEqual(0, link.A.Channel.Backlog);
            Assert.Greater(link.A.DatagramsSent, 40, "the datagram sequence crossed the wrap");
        }

        [Test]
        public void SequenceNumbers_SurviveAFullWrapOfTheDatagramSequence()
        {
            // 70,000 datagrams each way on a clean link: seq wraps past 65535 and acks keep working after it.
            var link = new Link(3, 60000);
            int next = 0;
            for (int i = 0; i < 70000; i++)
            {
                if (i % 7 == 0) Assert.IsTrue(link.A.Enqueue(next++));
                link.Now += 0.05;
                link.Hub.Advance(0.05);
                link.A.SendDatagram(link.Now);
                link.B.ReceiveAll();
                link.B.SendDatagram(link.Now);
                link.A.ReceiveAll();
            }
            AssertInOrderExactlyOnce(link.B.Received, next, "full wrap");
            Assert.AreEqual(10000, next);
            Assert.LessOrEqual(link.A.Channel.Backlog, 1);
            Assert.AreEqual(0, link.A.Channel.ResentMessages);
        }

        [Test]
        public void UnackedMessages_AreResentAfter100Ms_NotBefore()
        {
            var link = new Link(1);
            link.B.Transport.Blackhole = true; // B hears nothing
            link.A.Enqueue(0);
            link.A.SendDatagram(10.00);
            Assert.AreEqual(1, link.A.Channel.SentMessages);
            Assert.IsFalse(link.A.Channel.HasDue(10.05));
            link.A.SendDatagram(10.05);
            link.A.SendDatagram(10.099);
            Assert.AreEqual(1, link.A.Channel.SentMessages, "not resent within 100 ms");
            Assert.IsTrue(link.A.Channel.HasDue(10.10));
            link.A.SendDatagram(10.10);
            Assert.AreEqual(2, link.A.Channel.SentMessages, "resent at 100 ms");
            Assert.AreEqual(1, link.A.Channel.ResentMessages);
            link.A.SendDatagram(10.15);
            Assert.AreEqual(2, link.A.Channel.SentMessages);
            link.A.SendDatagram(10.12, true);
            Assert.AreEqual(3, link.A.Channel.SentMessages, "force resends at once (used for Bye and SessionClosed)");
            Assert.AreEqual(ReliableChannel.DefaultResendSeconds, link.A.Channel.ResendSeconds);
        }

        [Test]
        public void Resends_Repeat4TimesAt100Ms_ThenBackOffTo800Ms_AndResetBackoffMakesThemDueAgain()
        {
            var channel = new ReliableChannel(500);
            var buffer = new byte[256];
            Assert.IsTrue(channel.Enqueue(NetMsg.DoorState, new byte[] { 1, 2 }, 0, 2));
            var sendTimes = new List<double>();
            // Poll every 10 ms for 6 s, as a session does; nothing is ever acked.
            for (int step = 0; step <= 600; step++)
            {
                double now = 50.0 + step * 0.01;
                bool due = channel.HasDue(now);
                var w = new NetWriter(buffer);
                int written = channel.WriteDue(ref w, channel.NextSendSequence(), now);
                Assert.AreEqual(due ? 1 : 0, written, "HasDue and WriteDue agree at " + now);
                if (written == 1) sendTimes.Add(now - 50.0);
            }
            double[] expected = { 0.0, 0.1, 0.2, 0.3, 0.4, 0.6, 1.0, 1.8, 2.6, 3.4, 4.2, 5.0, 5.8 };
            Assert.AreEqual(expected.Length, sendTimes.Count, "sends in 6 s");
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], sendTimes[i], 1e-6, "send " + i);
            Assert.AreEqual(ReliableChannel.MaxResendSeconds, sendTimes[12] - sendTimes[11], 1e-6, "the wait is capped at 800 ms");

            // The peer is heard again 250 ms after the last copy went out: everything unacked is due at once and
            // back on the 100 ms interval.
            Assert.IsFalse(channel.HasDue(56.05));
            channel.ResetBackoff();
            Assert.IsTrue(channel.HasDue(56.05));
            var again = new NetWriter(buffer);
            Assert.AreEqual(1, channel.WriteDue(ref again, channel.NextSendSequence(), 56.05));
            Assert.IsFalse(channel.HasDue(56.14));
            Assert.IsTrue(channel.HasDue(56.15), "100 ms later, not 800");

            // a new message starts on the fast interval whatever the older one is doing
            Assert.IsTrue(channel.Enqueue(NetMsg.DoorState, new byte[] { 3 }, 0, 1));
            Assert.IsTrue(channel.HasDue(56.06));
        }

        [Test]
        public void AnAck_ForAnyOfTheLast16DatagramsThatCarriedAMessage_ClearsIt()
        {
            var buffer = new byte[256];
            for (int ackedCopy = 0; ackedCopy < ReliableChannel.SequenceMemory; ackedCopy++)
            {
                var channel = new ReliableChannel(65530); // the datagram sequence wraps during the test
                Assert.IsTrue(channel.Enqueue(NetMsg.DoorState, new byte[] { 1 }, 0, 1));
                var carriers = new List<ushort>();
                for (int copy = 0; copy < ReliableChannel.SequenceMemory; copy++)
                {
                    ushort seq = channel.NextSendSequence();
                    var w = new NetWriter(buffer);
                    Assert.AreEqual(1, channel.WriteDue(ref w, seq, 10.0 + copy, true));
                    carriers.Add(seq);
                    channel.NextSendSequence(); // a datagram in between that did not carry it
                }
                channel.OnAck((ushort)(carriers[ackedCopy] + 1), 0u);
                Assert.AreEqual(1, channel.Backlog, "an ack for a datagram that did not carry the message proves nothing");
                channel.OnAck(carriers[ackedCopy], 0u);
                Assert.AreEqual(0, channel.Backlog, "copy " + ackedCopy + " of 16 was acked: the message is done, however long the ack took");
            }

            // Only the newest 16 carriers are remembered: the 17th copy pushes the first one out.
            var old = new ReliableChannel(0);
            Assert.IsTrue(old.Enqueue(NetMsg.DoorState, new byte[] { 1 }, 0, 1));
            for (int copy = 0; copy <= ReliableChannel.SequenceMemory; copy++)
            {
                var w = new NetWriter(buffer);
                old.WriteDue(ref w, old.NextSendSequence(), 10.0 + copy, true);
            }
            old.OnAck(0, 0u);
            Assert.AreEqual(1, old.Backlog, "the first of 17 carriers has been forgotten");
            old.OnAck(1, 0u);
            Assert.AreEqual(0, old.Backlog, "the second is still remembered");
        }

        [Test]
        public void HighLatency_DoesNotStallTheStream_AcksArrivingManyResendPeriodsLate()
        {
            // One-way delays far above the 100 ms resend period. With only the last 4 carriers remembered and a
            // fixed 100 ms resend this used to livelock above about 200 ms: nothing was ever recognised as acked.
            double[] oneWay = { 0.25, 0.3, 0.5, 1.0 };
            double[] finishWithin = { 6.0, 7.0, 11.0, 21.0 };
            for (int k = 0; k < oneWay.Length; k++)
            {
                var link = new Link(5);
                link.Hub.Latency = oneWay[k];
                const int count = 300;
                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(link.A.Enqueue(i, 6));
                    Assert.IsTrue(link.B.Enqueue(i, 6));
                }
                double finished = -1;
                link.Run(finishWithin[k], step =>
                {
                    if (finished < 0 && link.A.Channel.Backlog == 0 && link.B.Channel.Backlog == 0) finished = link.Now;
                });
                string what = (oneWay[k] * 1000) + " ms one way";
                AssertInOrderExactlyOnce(link.B.Received, count, what + " A->B");
                AssertInOrderExactlyOnce(link.A.Received, count, what + " B->A");
                Assert.GreaterOrEqual(finished, 0, what + ": both backlogs drained within " + finishWithin[k] + " s");
                Assert.IsFalse(link.A.Channel.Overflowed || link.B.Channel.Overflowed);
            }
        }

        [Test]
        public void HighLatency_WithJitterAndLoss_StillDrains_WhileMessagesKeepComing()
        {
            // The Stage 5 soak condition and beyond it: 150 / 180 / 300 / 500 ms one way with jitter and 15% loss,
            // 10 new messages a second each way for 60 s. The backlog must stay small, not creep up to the cap.
            double[] oneWay = { 0.15, 0.18, 0.3, 0.5 };
            for (int k = 0; k < oneWay.Length; k++)
            {
                var link = new Link(21 + k);
                link.Hub.Latency = oneWay[k];
                link.Hub.Jitter = 0.03;
                link.Hub.Loss = 0.15;
                int next = 0, maxBacklog = 0;
                link.Run(60.0, step =>
                {
                    if (step % 10 == 0)
                    {
                        Assert.IsTrue(link.A.Enqueue(next));
                        Assert.IsTrue(link.B.Enqueue(next));
                        next++;
                    }
                    if (link.A.Channel.Backlog > maxBacklog) maxBacklog = link.A.Channel.Backlog;
                    if (link.B.Channel.Backlog > maxBacklog) maxBacklog = link.B.Channel.Backlog;
                });
                link.Run(15.0);
                string what = (oneWay[k] * 1000) + " ms one way";
                AssertInOrderExactlyOnce(link.B.Received, next, what + " A->B");
                AssertInOrderExactlyOnce(link.A.Received, next, what + " B->A");
                Assert.AreEqual(0, link.A.Channel.Backlog + link.B.Channel.Backlog, what + ": drained");
                Assert.Less(maxBacklog, 64, what + ": the backlog never even filled the in-flight window");
            }
        }

        [Test]
        public void AtMost64MessagesAreInFlight_AndTheBacklogCapIs512()
        {
            var link = new Link(1);
            link.B.Transport.Blackhole = true;
            for (int i = 0; i < ReliableChannel.MaxBacklog; i++) Assert.IsTrue(link.A.Enqueue(i, 4), "message " + i + " fits the backlog");
            Assert.AreEqual(512, link.A.Channel.Backlog);
            Assert.IsFalse(link.A.Channel.Overflowed);

            for (int i = 0; i < 20; i++) link.A.SendDatagram(1.0 + i * 0.2);
            Assert.AreEqual(64, link.A.Channel.SentMessages - link.A.Channel.ResentMessages, "only the first 64 ever leave while nothing is acked");

            Assert.IsFalse(link.A.Enqueue(999, 4), "the 513th message is refused");
            Assert.IsTrue(link.A.Channel.Overflowed, "and the channel reports the overflow so the session can drop the peer");

            // the peer wakes up: the whole backlog drains in order
            link.B.Transport.Blackhole = false;
            link.Now = 10;
            link.Run(10.0);
            AssertInOrderExactlyOnce(link.B.Received, 512, "backlog");
            Assert.AreEqual(0, link.A.Channel.Backlog);
        }

        [Test]
        public void TheLargestMessage_FitsOneDatagram_AndNothingExceeds1200Bytes()
        {
            var link = new Link(1);
            for (int i = 0; i < 6; i++) Assert.IsTrue(link.A.Enqueue(i, ReliableChannel.MaxMessageBytes));
            for (int i = 6; i < 60; i++) Assert.IsTrue(link.A.Enqueue(i, 150));
            link.Run(3.0);
            AssertInOrderExactlyOnce(link.B.Received, 60, "big messages");
            Assert.LessOrEqual(link.A.LargestDatagram, NetProtocol.MaxDatagram);
            Assert.GreaterOrEqual(link.A.LargestDatagram, ReliableChannel.MaxMessageBytes + ReliableChannel.Overhead + DatagramHeader.Size);
            Assert.AreEqual(0, link.Hub.OversizeCount);

            var payload = new byte[ReliableChannel.MaxMessageBytes + 1];
            Assert.IsFalse(link.A.Channel.Enqueue(NetMsg.RunStart, payload, 0, payload.Length), "a message over 1000 bytes is refused, not fragmented");
            Assert.IsFalse(link.A.Channel.Overflowed, "that is not a backlog overflow");
            Assert.IsFalse(link.A.Channel.Enqueue(NetMsg.RunStart, payload, 900, 200), "range outside the array");
            Assert.IsFalse(link.A.Channel.Enqueue(NetMsg.RunStart, null, 0, 4));
            Assert.IsTrue(link.A.Channel.Enqueue(NetMsg.Bye, null, 0, 0), "an empty payload is fine");
        }

        [Test]
        public void DuplicateAndStaleDatagrams_AreRejected_NewAndLateOnesAccepted()
        {
            var channel = new ReliableChannel(100);
            Assert.IsTrue(channel.OnDatagramReceived(100));
            Assert.IsFalse(channel.OnDatagramReceived(100), "same datagram twice");
            Assert.IsTrue(channel.OnDatagramReceived(103));
            Assert.IsTrue(channel.OnDatagramReceived(101), "late but new");
            Assert.IsFalse(channel.OnDatagramReceived(101));
            Assert.IsTrue(channel.OnDatagramReceived(102));
            channel.GetAck(out ushort ack, out uint bits);
            Assert.AreEqual(103, ack);
            Assert.AreEqual(0b111u, bits & 0b111u, "102, 101 and 100 are acked by the bits");

            Assert.IsTrue(channel.OnDatagramReceived(103 + 32));
            channel.GetAck(out ack, out bits);
            Assert.AreEqual(135, ack);
            Assert.AreEqual(1u << 31, bits & (1u << 31), "103 is the 32nd bit");
            Assert.IsFalse(channel.OnDatagramReceived(102), "more than 32 behind: dropped, the sender will resend");
            Assert.IsTrue(channel.OnDatagramReceived(135 + 200), "a jump forward clears the window");
            channel.GetAck(out ack, out bits);
            Assert.AreEqual(0u, bits);
            Assert.AreEqual(3, channel.DuplicateDatagrams);

            // across the wrap
            var wrap = new ReliableChannel(65534);
            Assert.IsTrue(wrap.OnDatagramReceived(65534));
            Assert.IsTrue(wrap.OnDatagramReceived(1));
            Assert.IsTrue(wrap.OnDatagramReceived(65535));
            Assert.IsTrue(wrap.OnDatagramReceived(0));
            Assert.IsFalse(wrap.OnDatagramReceived(65535));
            wrap.GetAck(out ack, out bits);
            Assert.AreEqual(1, ack);
            Assert.AreEqual(0b111u, bits);
        }

        [Test]
        public void ReliableMessages_OutOfOrder_AreHeldBack_DuplicatesIgnored_AndWindowEnforced()
        {
            var channel = new ReliableChannel(65534);
            var data = new byte[] { 1, 2, 3, 4 };
            Assert.IsTrue(channel.Accept(65535, 70, data, 0, 4));
            Assert.IsFalse(channel.TryDeliver(out _, out _, out _), "65534 has not arrived yet");
            Assert.IsTrue(channel.Accept(0, 71, data, 0, 4));
            Assert.IsFalse(channel.Accept(0, 71, data, 0, 4), "duplicate while buffered");
            Assert.IsTrue(channel.Accept(65534, 69, data, 1, 2));
            Assert.IsTrue(channel.TryDeliver(out byte type, out byte[] delivered, out int size));
            Assert.AreEqual(69, type);
            Assert.AreEqual(2, size);
            Assert.AreEqual(2, delivered[0]);
            Assert.IsTrue(channel.TryDeliver(out type, out _, out _));
            Assert.AreEqual(70, type);
            Assert.IsTrue(channel.TryDeliver(out type, out _, out _));
            Assert.AreEqual(71, type, "order holds across the wrap");
            Assert.IsFalse(channel.TryDeliver(out _, out _, out _));
            Assert.IsFalse(channel.Accept(65535, 70, data, 0, 4), "already delivered");
            Assert.IsFalse(channel.Accept((ushort)(1 + ReliableChannel.MaxInFlight), 70, data, 0, 4), "beyond the 64-message window");
            Assert.IsTrue(channel.Accept((ushort)(1 + ReliableChannel.MaxInFlight - 1), 70, data, 0, 4), "last slot of the window");
            Assert.AreEqual(3, channel.DeliveredMessages);
            Assert.AreEqual(2, channel.DuplicateMessages);
        }

        [Test]
        public void Channel_FedNonsense_NeverThrows()
        {
            var channel = new ReliableChannel();
            var random = new Random(4);
            var data = new byte[1200];
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 20000; i++)
                {
                    random.NextBytes(data);
                    channel.OnDatagramReceived((ushort)random.Next(0, 65536));
                    channel.OnAck((ushort)random.Next(0, 65536), (uint)random.Next());
                    channel.Accept((ushort)random.Next(0, 65536), (byte)random.Next(0, 256), data, random.Next(-5, 1300), random.Next(-5, 1300));
                    channel.Accept((ushort)random.Next(0, 65536), 70, null, 0, 10);
                    channel.TryDeliver(out _, out _, out _);
                    if (i % 50 == 0) channel.Enqueue(70, data, random.Next(-2, 1300), random.Next(-2, 1300));
                    var w = new NetWriter(data, 0, random.Next(0, 1200));
                    channel.WriteDue(ref w, (ushort)i, i * 0.01, i % 3 == 0);
                    channel.HasDue(i * 0.01);
                }
                channel.Reset(12345);
            });
        }

        // ------------------------------------------------------------------ MemoryTransport itself

        [Test]
        public void MemoryTransport_DeliversOnlyWhenTimeIsAdvanced_AndInSendOrder()
        {
            var hub = new MemoryTransportHub(1) { Latency = 0.08 };
            MemoryTransport a = hub.Create(), b = hub.Create();
            var buffer = new byte[64];
            for (byte i = 0; i < 5; i++) Assert.IsTrue(a.Send(b.Local, new[] { i }, 1));
            Assert.IsFalse(b.Receive(out _, buffer, out _), "nothing before the latency has passed");
            hub.Advance(0.079);
            Assert.IsFalse(b.Receive(out _, buffer, out _));
            hub.Advance(0.002);
            for (byte i = 0; i < 5; i++)
            {
                Assert.IsTrue(b.Receive(out NetEndpoint from, buffer, out int length));
                Assert.AreEqual(a.Local, from);
                Assert.AreEqual(1, length);
                Assert.AreEqual(i, buffer[0], "order preserved without reorder faults");
            }
            Assert.IsFalse(b.Receive(out _, buffer, out _));
            Assert.AreEqual(5, hub.DeliveredCount);
            Assert.AreEqual(0.081, hub.Now, 1e-9);
        }

        [Test]
        public void MemoryTransport_Faults_AreSeeded_AndRepeatExactly()
        {
            string Run(int seed)
            {
                var hub = new MemoryTransportHub(seed) { Loss = 0.3, Reorder = 0.3, Duplicate = 0.2, Latency = 0.01, Jitter = 0.02 };
                MemoryTransport a = hub.Create(), b = hub.Create();
                var buffer = new byte[64];
                var log = new System.Text.StringBuilder();
                for (int i = 0; i < 300; i++)
                {
                    a.Send(b.Local, new[] { (byte)i }, 1);
                    hub.Advance(0.005);
                    while (b.Receive(out _, buffer, out _)) log.Append(buffer[0]).Append(',');
                }
                hub.Advance(1);
                while (b.Receive(out _, buffer, out _)) log.Append(buffer[0]).Append(',');
                Assert.Greater(hub.LostCount, 50);
                Assert.Greater(hub.DuplicatedCount, 20);
                return log.ToString();
            }
            string first = Run(42);
            Assert.AreEqual(first, Run(42), "same seed, same faults, same arrival order");
            Assert.AreNotEqual(first, Run(43));
            Assert.AreNotEqual(string.Join(",", System.Linq.Enumerable.Range(0, 300)) + ",", first, "arrivals really were disturbed");
        }

        [Test]
        public void MemoryTransport_BroadcastReachesEverySocketOnThatPort_ButNotTheSender()
        {
            var hub = new MemoryTransportHub(1);
            MemoryTransport host1 = hub.Create(NetProtocol.HostPort);
            MemoryTransport host2 = hub.Create(NetProtocol.HostPort);
            MemoryTransport other = hub.Create(12345);
            MemoryTransport far = hub.Create(new NetEndpoint(192, 168, 7, 20, NetProtocol.HostPort));
            MemoryTransport sender = hub.Create(NetProtocol.HostPort);
            var buffer = new byte[64];

            Assert.IsTrue(sender.Send(NetEndpoint.Broadcast(NetProtocol.HostPort), new byte[] { 9 }, 1));
            hub.Advance(0);
            Assert.IsTrue(host1.Receive(out NetEndpoint from, buffer, out _));
            Assert.AreEqual(sender.Local, from);
            Assert.IsTrue(host2.Receive(out _, buffer, out _));
            Assert.IsTrue(far.Receive(out _, buffer, out _), "255.255.255.255 reaches every subnet on the hub");
            Assert.IsFalse(other.Receive(out _, buffer, out _), "different port");
            Assert.IsFalse(sender.Receive(out _, buffer, out _), "not echoed to the sender");

            Assert.IsTrue(sender.Send(NetEndpoint.SubnetBroadcast(sender.Local.address, NetProtocol.HostPort), new byte[] { 9 }, 1));
            hub.Advance(0);
            Assert.IsTrue(host1.Receive(out _, buffer, out _));
            Assert.IsFalse(far.Receive(out _, buffer, out _), "a.b.c.255 stays inside the /24");

            sender.BroadcastBlocked = true;
            sender.Send(NetEndpoint.Broadcast(NetProtocol.HostPort), new byte[] { 9 }, 1);
            hub.Advance(0);
            Assert.IsFalse(host1.Receive(out _, buffer, out _), "a network that drops broadcast");
            Assert.IsTrue(sender.Send(host1.Local, new byte[] { 9 }, 1));
            hub.Advance(0);
            Assert.IsTrue(host1.Receive(out _, buffer, out _), "unicast still works");
        }

        [Test]
        public void MemoryTransport_RefusesOversizeDatagrams_FailedBinds_AndCleansUp()
        {
            var hub = new MemoryTransportHub(1);
            MemoryTransport a = hub.Create(NetProtocol.HostPort);
            Assert.IsNull(hub.Create(a.Local), "binding a taken endpoint fails like PORT BUSY");
            MemoryTransport b = hub.Create();
            Assert.AreEqual(2, hub.LiveCount);
            Assert.IsFalse(a.Send(b.Local, new byte[2000], 1201), "over the 1200-byte datagram cap");
            Assert.AreEqual(1, hub.OversizeCount);
            Assert.IsTrue(a.Send(b.Local, new byte[2000], 1200));
            Assert.IsFalse(a.Send(b.Local, null, 4));
            Assert.IsFalse(a.Send(b.Local, new byte[4], 9));
            hub.Advance(0);
            Assert.IsFalse(b.Receive(out _, new byte[100], out _), "a datagram bigger than the caller's buffer is dropped");

            a.Send(new NetEndpoint(10, 9, 9, 9, 1), new byte[4], 4);
            hub.Advance(0);
            Assert.AreEqual(1, hub.LostCount, "nobody there");

            b.Dispose();
            b.Dispose();
            Assert.IsTrue(b.IsDisposed);
            Assert.IsFalse(b.Send(a.Local, new byte[4], 4));
            Assert.IsFalse(b.Receive(out _, new byte[100], out _));
            a.Dispose();
            Assert.AreEqual(0, hub.LiveCount);
        }
    }
}
