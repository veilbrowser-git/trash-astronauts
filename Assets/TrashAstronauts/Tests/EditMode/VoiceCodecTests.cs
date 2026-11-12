using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core.Voice;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Proximity voice chat, pure half: codec, resampler, voice gate, echo suppressor, jitter buffer, ring and
    /// rolloff. Everything is driven with synthetic speech-like signals; no microphone, no Unity audio.
    /// </summary>
    public class VoiceCodecTests
    {
        // ------------------------------------------------------------------ synthetic speech

        /// <summary>
        /// A speech-like signal: a voiced source whose pitch glides (110..190 Hz) through three formant bumps
        /// (moving like vowels change), with a 4 Hz syllable envelope and short pauses, about -22 dBFS RMS while
        /// "talking".
        /// </summary>
        static float[] Speech(int rate, double seconds, int seed = 1, float level = 1f, bool pauses = true)
        {
            int n = (int)(rate * seconds);
            var x = new float[n];
            var random = new Random(seed);
            double phase = 0.0;
            double[] f1 = { 700, 400, 300, 600 };
            double[] f2 = { 1200, 2000, 900, 1700 };
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                double f0 = 150 + 40 * Math.Sin(2 * Math.PI * 0.7 * t + seed);
                phase += 2 * Math.PI * f0 / rate;
                int vowel = (int)(t * 3.3 + seed) % 4;
                double sum = 0.0;
                for (int k = 1; k * f0 < 3800; k++)
                {
                    double f = k * f0;
                    double amp = Math.Exp(-Math.Pow((f - f1[vowel]) / 180, 2)) + 0.6 * Math.Exp(-Math.Pow((f - f2[vowel]) / 250, 2))
                                 + 0.25 * Math.Exp(-Math.Pow((f - 2700) / 300, 2)) + 0.08 / k;
                    sum += amp * Math.Sin(k * phase + k * 0.7);
                }
                double envelope = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
                if (pauses && (t % 1.3) > 1.05) envelope = 0.0;
                x[i] = (float)(0.09 * level * envelope * sum + 0.0005 * (random.NextDouble() - 0.5));
            }
            return x;
        }

        static float[] Sine(int rate, double seconds, double hz, float amp)
        {
            var x = new float[(int)(rate * seconds)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
            return x;
        }

        static float[] Resample(float[] x, int from, int to)
        {
            var r = new VoiceResampler(from, to);
            var y = new float[r.MaxOutput(x.Length)];
            int n = r.Process(x, 0, x.Length, y, 0, y.Length);
            Array.Resize(ref y, n);
            return y;
        }

        static float[] RoundTrip(float[] x)
        {
            var encoder = new VoiceEncoder();
            var decoder = new VoiceDecoder();
            var packet = new byte[VoiceFormat.FrameBytes];
            int frames = x.Length / VoiceFormat.FrameSamples;
            var y = new float[frames * VoiceFormat.FrameSamples];
            for (int f = 0; f < frames; f++)
            {
                Assert.AreEqual(VoiceFormat.FrameBytes, encoder.Encode(x, f * VoiceFormat.FrameSamples, packet, 0));
                Assert.IsTrue(decoder.Decode(packet, 0, packet.Length, y, f * VoiceFormat.FrameSamples));
            }
            return y;
        }

        /// <summary>
        /// Signal-to-noise ratio of <paramref name="y"/> against <paramref name="x"/> after the best alignment. Both are
        /// first raised to 48 kHz through the same filter, so the codec's 42.33-sample delay lines up exactly.
        /// </summary>
        static double AlignedSnr(float[] x, float[] y, int rate, out int lag, out double gain)
        {
            float[] a = Resample(x, rate, rate * 3);
            float[] b = Resample(y, rate, rate * 3);
            int skip = rate * 3 / 10; // the first 100 ms (filters settling)
            int length = Math.Min(a.Length, b.Length) - skip - 600;
            double best = double.MinValue;
            lag = 0;
            for (int l = 0; l < 500; l++)
            {
                double c = 0.0;
                for (int i = skip; i < skip + length; i++) c += a[i] * b[i + l];
                if (c > best) { best = c; lag = l; }
            }
            double xy = 0.0, yy = 0.0, xx = 0.0;
            for (int i = skip; i < skip + length; i++)
            {
                xy += a[i] * b[i + lag];
                yy += b[i + lag] * b[i + lag];
                xx += a[i] * a[i];
            }
            gain = xy / Math.Max(1e-12, yy);
            double err = 0.0;
            for (int i = skip; i < skip + length; i++)
            {
                double e = a[i] - gain * b[i + lag];
                err += e * e;
            }
            return 10.0 * Math.Log10(xx / Math.Max(1e-12, err));
        }

        // ------------------------------------------------------------------ codec

        [Test]
        public void Format_NumbersAreAsDocumented_AndOneSpeakerStaysUnderNineKilobytesASecond()
        {
            Assert.AreEqual(320, VoiceFormat.FrameSamples);
            Assert.AreEqual(240, VoiceFormat.CodecSamples);
            Assert.AreEqual(123, VoiceFormat.FrameBytes);
            Assert.AreEqual(6150, VoiceFormat.PayloadBytesPerSecond);
            // per datagram: session header 16, type 1, Voice header 4, two frames each with a length byte, End 1
            int datagram = 16 + 1 + 4 + VoiceFormat.FramesPerPacket * (1 + VoiceFormat.FrameBytes) + 1;
            Assert.AreEqual(270, datagram);
            double perSecond = datagram * (1000.0 / (VoiceFormat.FrameMs * VoiceFormat.FramesPerPacket));
            Assert.AreEqual(6750.0, perSecond, 0.01);
            Assert.Less(perSecond + 28 * 25, 9000.0, "with UDP/IP headers too");
        }

        [Test]
        public void Adpcm_AtTheCodecRate_KeepsSpeechClean()
        {
            float[] x = Speech(VoiceFormat.CodecRate, 2.0);
            var bytes = new byte[VoiceFormat.FrameBytes];
            var y = new float[x.Length];
            int predictor = 0, index = 0;
            for (int f = 0; f + VoiceFormat.CodecSamples <= x.Length; f += VoiceFormat.CodecSamples)
            {
                Assert.AreEqual(VoiceFormat.FrameBytes, ImaAdpcm.EncodeBlock(x, f, VoiceFormat.CodecSamples, bytes, 0, ref predictor, ref index));
                Assert.IsTrue(ImaAdpcm.DecodeBlock(bytes, 0, VoiceFormat.CodecSamples, y, f));
            }
            double signal = 0, noise = 0;
            for (int i = 1200; i < x.Length; i++)
            {
                signal += x[i] * x[i];
                noise += (x[i] - y[i]) * (x[i] - y[i]);
            }
            double snr = 10 * Math.Log10(signal / noise);
            TestContext.WriteLine("ADPCM SNR " + snr.ToString("F1") + " dB");
            Assert.Greater(snr, 18.0);
        }

        [Test]
        public void RoundTrip_SpeechLikeSignals_HaveAGoodSignalToNoiseRatio()
        {
            foreach (int seed in new[] { 1, 2, 3 })
            {
                foreach (float level in new[] { 0.25f, 1f, 3f })
                {
                    float[] x = Speech(VoiceFormat.SampleRate, 2.0, seed, level);
                    float[] y = RoundTrip(x);
                    double snr = AlignedSnr(x, y, VoiceFormat.SampleRate, out int lag, out double gain);
                    TestContext.WriteLine("seed " + seed + " level " + level + ": SNR " + snr.ToString("F1") + " dB, lag " + lag + "/3 samples, gain " + gain.ToString("F3"));
                    Assert.Greater(snr, 15.0, "seed " + seed + " level " + level);
                    Assert.That(gain, Is.InRange(0.85, 1.15), "the level comes back unchanged");
                    Assert.That(lag, Is.InRange(120, 135), "about 42 samples (2.6 ms) of codec delay");
                }
            }
        }

        [Test]
        public void EveryFrame_DecodesOnItsOwn_SoALostFrameDoesNotCorruptTheNext()
        {
            float[] x = Speech(VoiceFormat.SampleRate, 1.0);
            var encoder = new VoiceEncoder();
            var frames = new List<byte[]>();
            for (int f = 0; f + VoiceFormat.FrameSamples <= x.Length; f += VoiceFormat.FrameSamples)
            {
                var b = new byte[VoiceFormat.FrameBytes];
                encoder.Encode(x, f, b, 0);
                frames.Add(b);
            }
            // The ADPCM part of frame 30 decoded after frame 29 (in order) and on its own is identical.
            var inOrder = new float[VoiceFormat.CodecSamples];
            var alone = new float[VoiceFormat.CodecSamples];
            Assert.IsTrue(ImaAdpcm.DecodeBlock(frames[30], 0, VoiceFormat.CodecSamples, inOrder, 0));
            Assert.IsTrue(ImaAdpcm.DecodeBlock(frames[30], 0, VoiceFormat.CodecSamples, alone, 0));
            CollectionAssert.AreEqual(inOrder, alone);

            // Skipping half the frames on the decoder side still gives sensible audio for the ones decoded.
            var decoder = new VoiceDecoder();
            var out1 = new float[VoiceFormat.FrameSamples];
            for (int i = 0; i < frames.Count; i += 2)
            {
                Assert.IsTrue(decoder.Decode(frames[i], 0, frames[i].Length, out1, 0));
                foreach (float s in out1) Assert.IsFalse(float.IsNaN(s) || Math.Abs(s) > 1.5f);
            }

            // Garbage is refused without throwing: wrong length or an impossible step index.
            Assert.IsFalse(decoder.Decode(frames[0], 0, 10, out1, 0));
            var bad = (byte[])frames[0].Clone();
            bad[2] = 200;
            Assert.IsFalse(decoder.Decode(bad, 0, bad.Length, out1, 0));
            var random = new Random(5);
            var junk = new byte[VoiceFormat.FrameBytes];
            for (int i = 0; i < 200; i++)
            {
                random.NextBytes(junk);
                junk[2] = (byte)random.Next(0, 89);
                Assert.IsTrue(decoder.Decode(junk, 0, junk.Length, out1, 0));
                foreach (float s in out1) Assert.IsFalse(float.IsNaN(s) || float.IsInfinity(s));
            }
        }

        [Test]
        public void Encoder_ClipsInsteadOfWrapping_AndIgnoresNaN()
        {
            var x = new float[VoiceFormat.FrameSamples * 10];
            for (int i = 0; i < x.Length; i++) x[i] = (i / 20) % 2 == 0 ? 4f : -4f;
            x[100] = float.NaN;
            float[] y = RoundTrip(x);
            foreach (float s in y) Assert.IsFalse(float.IsNaN(s));
            Assert.Less(Math.Abs(y[y.Length - 1]), 1.2f);
        }

        // ------------------------------------------------------------------ resampler

        static double ToneLevel(float[] y, int rate, double hz, int skip)
        {
            double c = 0, s = 0;
            int n = y.Length - skip;
            for (int i = skip; i < y.Length; i++)
            {
                c += y[i] * Math.Cos(2 * Math.PI * hz * i / rate);
                s += y[i] * Math.Sin(2 * Math.PI * hz * i / rate);
            }
            return 2 * Math.Sqrt(c * c + s * s) / n;
        }

        [TestCase(48000, 16000)]
        [TestCase(44100, 16000)]
        [TestCase(16000, 12000)]
        [TestCase(12000, 16000)]
        [TestCase(8000, 16000)]
        [TestCase(16000, 48000)]
        [TestCase(22050, 16000)]
        public void Resampler_KeepsATone_AtItsFrequencyAndLevel(int from, int to)
        {
            float[] x = Sine(from, 1.0, 1000.0, 0.5f);
            float[] y = Resample(x, from, to);
            Assert.AreEqual(to, y.Length, 3, "one second in is one second out");
            double level = ToneLevel(y, to, 1000.0, to / 10);
            Assert.AreEqual(0.5, level, 0.03, "1 kHz passes at its level");
            double neighbour = ToneLevel(y, to, 1100.0, to / 10);
            Assert.Less(neighbour, 0.02, "and only at 1 kHz");
        }

        [Test]
        public void Resampler_RemovesWhatTheLowerRateCannotHold()
        {
            // 11 kHz at 48 kHz would fold to 5 kHz at 16 kHz; the low-pass must remove it.
            float[] y = Resample(Sine(48000, 1.0, 11000.0, 0.5f), 48000, 16000);
            Assert.Less(VoiceFormat.Rms(y, 1600, y.Length - 1600), 0.005f);
            // 7 kHz at 16 kHz folds to 5 kHz at 12 kHz.
            float[] z = Resample(Sine(16000, 1.0, 7000.0, 0.5f), 16000, 12000);
            Assert.Less(VoiceFormat.Rms(z, 1200, z.Length - 1200), 0.01f);
        }

        [Test]
        public void Resampler_GivesTheSameResult_WhateverTheBlockSizes()
        {
            float[] x = Speech(48000, 0.5);
            float[] whole = Resample(x, 48000, 16000);
            var r = new VoiceResampler(48000, 16000, 300);
            var pieces = new List<float>();
            var buffer = new float[4096];
            var random = new Random(9);
            int at = 0;
            while (at < x.Length)
            {
                int n = Math.Min(x.Length - at, random.Next(1, 900));
                int produced = r.Process(x, at, n, buffer, 0, buffer.Length);
                for (int i = 0; i < produced; i++) pieces.Add(buffer[i]);
                at += n;
            }
            Assert.AreEqual(whole.Length, pieces.Count);
            for (int i = 0; i < whole.Length; i++) Assert.AreEqual(whole[i], pieces[i], 1e-6f, "sample " + i);

            var same = new VoiceResampler(16000, 16000);
            Assert.IsTrue(same.IsPassthrough);
            Assert.AreEqual(5, same.Process(new float[] { 1, 2, 3, 4, 5 }, 0, 5, buffer, 0, buffer.Length));
            Assert.AreEqual(3f, buffer[2]);
        }

        // ------------------------------------------------------------------ voice gate and capture

        sealed class Recorder
        {
            public readonly List<ushort> Sequences = new List<ushort>();
            public readonly List<int> FrameCounts = new List<int>();
            public bool Accept = true;

            public bool Sink(ushort first, byte[] frames, int offset, int frameBytes, int count)
            {
                Assert.AreEqual(VoiceFormat.FrameBytes, frameBytes);
                Assert.That(count, Is.InRange(1, VoiceFormat.FramesPerPacket));
                FrameCounts.Add(count);
                for (int i = 0; i < count; i++) Sequences.Add((ushort)(first + i));
                return Accept;
            }
        }

        static void FeedInBlocks(VoiceCapture capture, float[] x, int rate, ref double now, Recorder recorder, bool allow = true, int block = 256)
        {
            for (int i = 0; i + block <= x.Length; i += block)
            {
                capture.Feed(x, i, block, rate, now, allow, recorder.Sink);
                now += block / (double)rate;
            }
        }

        static float[] Noise(int rate, double seconds, float rms, int seed)
        {
            var random = new Random(seed);
            var x = new float[(int)(rate * seconds)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)((random.NextDouble() * 2 - 1) * rms * 1.732);
            return x;
        }

        [Test]
        public void Capture_SilenceAndASteadyFan_SendNothing()
        {
            var capture = new VoiceCapture();
            var recorder = new Recorder();
            double now = 0;
            FeedInBlocks(capture, new float[48000 * 3], 48000, ref now, recorder);
            Assert.AreEqual(0, recorder.Sequences.Count, "silence is never sent");
            // A fan switches on (-40 dBFS, after digital silence): learned as background within about 2 s.
            FeedInBlocks(capture, Noise(48000, 10.0, 0.01f, 3), 48000, ref now, recorder);
            Assert.Less(recorder.Sequences.Count, 110, "at most about 2 s of the fan's start");
            int early = recorder.Sequences.Count;
            FeedInBlocks(capture, Noise(48000, 10.0, 0.01f, 4), 48000, ref now, recorder);
            Assert.AreEqual(early, recorder.Sequences.Count, "then never again");
            Assert.Greater(capture.FramesCaptured, 1000);
            // Talking over the fan still goes through.
            FeedInBlocks(capture, Speech(48000, 1.0, level: 2f, pauses: false), 48000, ref now, recorder);
            Assert.Greater(recorder.Sequences.Count, early + 40);
        }

        [Test]
        public void Capture_Talking_IsSentInPairsOfFrames_WithPreRollAndHangOver_ThenStops()
        {
            var capture = new VoiceCapture(65530); // also crosses the 16-bit wrap
            var recorder = new Recorder();
            double now = 0;
            FeedInBlocks(capture, Noise(48000, 1.0, 0.0005f, 1), 48000, ref now, recorder);
            Assert.AreEqual(0, recorder.Sequences.Count);
            long before = capture.FramesCaptured;
            ushort firstSpeechFrame = capture.NextSequence;
            FeedInBlocks(capture, Speech(48000, 1.0, pauses: false), 48000, ref now, recorder);
            Assert.IsTrue(capture.IsTransmitting);
            FeedInBlocks(capture, Noise(48000, 1.0, 0.0005f, 2), 48000, ref now, recorder);
            Assert.IsFalse(capture.IsTransmitting, "it stops after the hang-over");

            Assert.Greater(recorder.Sequences.Count, 50);
            Assert.Less(recorder.Sequences.Count, 50 + 2 + 18, "one second of speech, the pre-roll and the hang-over");
            for (int i = 1; i < recorder.Sequences.Count; i++) Assert.AreEqual((ushort)(recorder.Sequences[i - 1] + 1), recorder.Sequences[i], "contiguous numbering");
            Assert.LessOrEqual(VoiceFormat.SeqDelta(recorder.Sequences[0], firstSpeechFrame), 0, "the pre-roll starts at or before the first loud frame");
            Assert.IsTrue(recorder.FrameCounts.TrueForAll(c => c == 2 || c == 1));
            Assert.LessOrEqual(recorder.FrameCounts.FindAll(c => c == 1).Count, 2, "single-frame packets only at the edges");
            Assert.Greater(capture.FramesCaptured - before, 90);
        }

        [Test]
        public void Gate_AClickDoesNotOpen_ButTwoLoudFramesDo_AndTheHangOverIs300ms()
        {
            var gate = new VoiceActivityGate();
            for (int i = 0; i < 100; i++) gate.Process(-65f);
            Assert.IsFalse(gate.Process(-15f), "one loud frame (a click)");
            Assert.IsFalse(gate.Process(-65f));
            Assert.IsFalse(gate.Process(-20f));
            Assert.IsTrue(gate.Process(-20f), "two in a row");
            Assert.IsTrue(gate.JustOpened);
            int open = 0;
            while (gate.Process(-65f)) open++;
            Assert.AreEqual(15, open, "300 ms of hang-over");
            // a whisper 6 dB above the room does not open it, nor does anything under -54 dBFS
            for (int i = 0; i < 20; i++) Assert.IsFalse(gate.Process(-59f));
            for (int i = 0; i < 300; i++) gate.Process(-80f);
            for (int i = 0; i < 20; i++) Assert.IsFalse(gate.Process(-56f));
        }

        [Test]
        public void Capture_SendsNothing_WhenNotAllowed_AndAFailedSendIsCounted()
        {
            var capture = new VoiceCapture();
            var recorder = new Recorder();
            double now = 0;
            FeedInBlocks(capture, Speech(16000, 1.0, pauses: false), 16000, ref now, recorder, allow: false);
            Assert.AreEqual(0, recorder.Sequences.Count);
            Assert.IsFalse(capture.IsTransmitting);
            recorder.Accept = false;
            FeedInBlocks(capture, Speech(16000, 0.5, pauses: false), 16000, ref now, recorder);
            Assert.Greater(capture.PacketsFailed, 0);
            Assert.AreEqual(0, capture.FramesSent);
        }

        // ------------------------------------------------------------------ echo

        [Test]
        public void Echo_PlaybackExplainsAQuieterMic_ButNotClearlyLouderTalk_AndTheHoldEnds()
        {
            var echo = new VoiceEchoSuppressor();
            double now = 10;
            Assert.IsFalse(echo.IsEcho(-30f, now), "nothing plays: nothing is echo");
            echo.AddPlayback(-20f, now);
            Assert.IsTrue(echo.PlaybackActive(now));
            Assert.IsTrue(echo.IsEcho(-26f, now), "6 dB under what plays: the speakers");
            Assert.IsTrue(echo.IsEcho(-16f, now), "within the margin: still treated as the speakers");
            Assert.IsFalse(echo.IsEcho(-8f, now), "clearly louder: the player talking over it");
            Assert.IsTrue(echo.IsEcho(-26f, now + 0.3), "held for the output latency and room tail");
            Assert.IsFalse(echo.IsEcho(-26f, now + 0.45), "and released after the hold");
            echo.AddPlayback(-70f, now + 1);
            Assert.IsFalse(echo.PlaybackActive(now + 1), "very quiet playback does not count");
        }

        [Test]
        public void Echo_LearnsAQuietCoupling_DownFromTheSafeStart_ButNeverFromThePlayersOwnTalking()
        {
            var echo = new VoiceEchoSuppressor();
            double now = 0;
            // The player has talked a bit with nothing playing: their level is learned (about -12 dBFS).
            for (int i = 0; i < 200; i++, now += 0.02) echo.ProcessFrame(-12f, true, now);
            Assert.AreEqual(-12f, echo.TalkLevelDb, 1f);
            Assert.AreEqual(0f, echo.CouplingDb, 0.01f);
            // Playback at -20 dBFS leaks into the mic at -40 (coupling -20 dB) for 60 s.
            for (int i = 0; i < 3000; i++, now += 0.02)
            {
                echo.AddPlayback(-20f, now);
                echo.ProcessFrame(-40f + (i % 5), false, now);
            }
            Assert.That(echo.CouplingDb, Is.InRange(-22f, -14f), "the coupling follows the upper envelope of the leak");
            // Talking (at their usual level) over the playback goes through, and does not teach it anything.
            float coupling = echo.CouplingDb;
            for (int i = 0; i < 100; i++, now += 0.02)
            {
                echo.AddPlayback(-20f, now);
                Assert.IsFalse(echo.ProcessFrame(-12f, true, now), "double talk passes");
            }
            Assert.AreEqual(coupling, echo.CouplingDb, 0.6f);
            // A leak that suddenly gets louder (volume turned up) is caught within a few frames.
            int caught = 0;
            for (int i = 0; i < 25; i++, now += 0.02)
            {
                echo.AddPlayback(-20f, now);
                if (echo.ProcessFrame(-28f, false, now)) caught++;
            }
            Assert.Greater(caught, 15);
        }

        // ------------------------------------------------------------------ jitter buffer

        static byte[][] EncodeFrames(int count, out float[] source)
        {
            source = Speech(VoiceFormat.SampleRate, count * VoiceFormat.FrameSeconds + 0.1, pauses: false);
            var encoder = new VoiceEncoder();
            var frames = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                frames[i] = new byte[VoiceFormat.FrameBytes];
                encoder.Encode(source, i * VoiceFormat.FrameSamples, frames[i], 0);
            }
            return frames;
        }

        [Test]
        public void Jitter_ACleanStream_PlaysEveryFrameInOrder_AfterTheTargetDelay()
        {
            byte[][] frames = EncodeFrames(100, out _);
            var stream = new VoiceStream();
            var output = new float[VoiceFormat.FrameSamples];
            var results = new List<VoicePull>();
            double now = 0;
            ushort first = 40000;
            for (int slot = 0; slot < 130; slot++, now += 0.02)
            {
                // two frames per packet, every 40 ms
                if (slot % 2 == 1 && slot < 100)
                {
                    stream.Push((ushort)(first + slot - 1), frames[slot - 1], 0, frames[slot - 1].Length, now);
                    stream.Push((ushort)(first + slot), frames[slot], 0, frames[slot].Length, now);
                }
                results.Add(stream.Pull(output, 0, now));
            }
            int start = results.IndexOf(VoicePull.Played);
            Assert.That(start * 20, Is.InRange(60, 120), "about 80 ms of buffering before the first frame");
            Assert.AreEqual(100, stream.Jitter.Played);
            Assert.AreEqual(100, results.FindAll(r => r == VoicePull.Played).Count);
            for (int i = start; i < start + 100; i++) Assert.AreEqual(VoicePull.Played, results[i], "no gap at slot " + i);
            Assert.IsFalse(stream.Jitter.IsPlaying, "the spurt ends after the last frame");
            Assert.AreEqual(4, stream.Jitter.TargetDelayFrames);
            Assert.AreEqual(1, stream.Jitter.Spurts);
        }

        [Test]
        public void Jitter_ThirtyPercentLoss_Reordering_AndDuplicates_StillGiveContinuousAudio()
        {
            byte[][] frames = EncodeFrames(500, out _);
            var stream = new VoiceStream();
            var output = new float[VoiceFormat.FrameSamples];
            var random = new Random(77);
            // Packets of two frames; 30% lost; the rest arrive 0..60 ms late (so they reorder); 5% twice.
            var arrivals = new List<(double time, int frame)>();
            for (int p = 0; p < 250; p++)
            {
                if (random.NextDouble() < 0.3) continue;
                double t = (p * 2 + 1) * 0.02 + random.NextDouble() * 0.06;
                arrivals.Add((t, p * 2));
                arrivals.Add((t, p * 2 + 1));
                if (random.NextDouble() < 0.05) { arrivals.Add((t + 0.01, p * 2)); arrivals.Add((t + 0.01, p * 2 + 1)); }
            }
            arrivals.Sort((a, b) => a.time.CompareTo(b.time));
            int next = 0, silentInside = 0, played = 0, concealed = 0;
            bool started = false;
            double now = 0;
            for (int slot = 0; slot < 560; slot++, now += 0.02)
            {
                while (next < arrivals.Count && arrivals[next].time <= now)
                {
                    int f = arrivals[next].frame;
                    Assert.DoesNotThrow(() => stream.Push((ushort)f, frames[f], 0, frames[f].Length, now));
                    next++;
                }
                VoicePull r = stream.Pull(output, 0, now);
                foreach (float s in output) Assert.IsFalse(float.IsNaN(s));
                if (r == VoicePull.Played) { played++; started = true; }
                else if (r == VoicePull.Concealed) concealed++;
                else if (started && slot < 500) silentInside++;
            }
            TestContext.WriteLine("played " + played + ", concealed " + concealed + ", silent mid-stream " + silentInside + ", late " + stream.Jitter.Late + ", duplicates " + stream.Jitter.Duplicates + ", target " + stream.Jitter.TargetDelayFrames);
            Assert.Greater(played, 300, "about 70% of 500 frames");
            Assert.Greater(stream.Jitter.Duplicates, 0, "duplicates were dropped");
            Assert.LessOrEqual(stream.Jitter.Late, 20, "the delay absorbs 60 ms of jitter");
            Assert.Less(silentInside, 25, "the stream does not keep stopping and restarting");
            Assert.GreaterOrEqual(stream.Jitter.TargetDelayFrames, 4);
            Assert.GreaterOrEqual(stream.Jitter.JitterSeconds, 0.05, "the 60 ms of jitter was measured");
        }

        [Test]
        public void Jitter_TheTargetDelay_Adapts_Between80And120ms()
        {
            var bytes = new byte[VoiceFormat.FrameBytes];
            var calm = new VoiceJitterBuffer();
            var rough = new VoiceJitterBuffer();
            var random = new Random(4);
            for (int f = 0; f < 200; f++)
            {
                double sent = (f / 2 * 2 + 1) * VoiceFormat.FrameSeconds;
                calm.Push((ushort)f, bytes, 0, bytes.Length, sent + 0.001 * random.NextDouble());
                rough.Push((ushort)f, bytes, 0, bytes.Length, sent + 0.25 * random.NextDouble());
            }
            Assert.AreEqual(4, calm.TargetDelayFrames, "80 ms on a calm network");
            Assert.AreEqual(6, rough.TargetDelayFrames, "never more than 120 ms, however rough");
            var medium = new VoiceJitterBuffer();
            for (int f = 0; f < 200; f++) medium.Push((ushort)f, bytes, 0, bytes.Length, (f / 2 * 2 + 1) * VoiceFormat.FrameSeconds + (f % 7 == 0 ? 0.075 : 0.0));
            Assert.AreEqual(6, medium.TargetDelayFrames, "75 ms spikes need the full 120 ms");
        }

        [Test]
        public void Jitter_ConcealsAGap_ByFadingTheLastFrame_ThenSilence()
        {
            byte[][] frames = EncodeFrames(20, out _);
            var jitter = new VoiceJitterBuffer();
            var decoder = new VoiceDecoder();
            var output = new float[VoiceFormat.FrameSamples];
            for (int i = 0; i < 6; i++) jitter.Push((ushort)i, frames[i], 0, frames[i].Length, 0);
            var levels = new List<float>();
            var kinds = new List<VoicePull>();
            for (int i = 0; i < 20; i++)
            {
                // frames 6..11 never come; 12..17 arrive while the gap is being concealed
                if (i == 9) for (int k = 12; k < 18; k++) jitter.Push((ushort)k, frames[k], 0, frames[k].Length, 0.18);
                kinds.Add(jitter.Pull(decoder, output));
                levels.Add(VoiceFormat.Rms(output, 0, output.Length));
            }
            Assert.AreEqual(VoicePull.Played, kinds[5]);
            for (int i = 6; i < 12; i++) Assert.AreEqual(VoicePull.Concealed, kinds[i]);
            Assert.Greater(levels[6], 0.1f * levels[5], "the first missing frame repeats the last one");
            Assert.Less(levels[7], levels[6]);
            Assert.AreEqual(0f, levels[9], "silence after three");
            Assert.AreEqual(VoicePull.Played, kinds[12], "and plays again when frames return");
        }

        [Test]
        public void Jitter_DropsLateAndDuplicateFrames_CatchesUpAfterABurst_AndIsBounded()
        {
            byte[][] frames = EncodeFrames(200, out _);
            var jitter = new VoiceJitterBuffer();
            var decoder = new VoiceDecoder();
            var output = new float[VoiceFormat.FrameSamples];
            for (int i = 0; i < 4; i++) jitter.Push((ushort)i, frames[i], 0, frames[i].Length, i * 0.02);
            for (int i = 0; i < 3; i++) Assert.AreEqual(VoicePull.Played, jitter.Pull(decoder, output));
            Assert.IsFalse(jitter.Push(1, frames[1], 0, frames[1].Length, 0.1), "already played: late");
            Assert.AreEqual(1, jitter.Late);
            Assert.IsFalse(jitter.Push(3, frames[3], 0, frames[3].Length, 0.1), "still waiting: duplicate");
            Assert.AreEqual(1, jitter.Duplicates);
            Assert.IsFalse(jitter.Push(9, frames[9], 0, 0, 0.1), "empty");
            Assert.IsFalse(jitter.Push(9, new byte[500], 0, 500, 0.1), "oversized");

            // A burst: 20 frames arrive at once after a stall. It skips ahead instead of staying 400 ms behind.
            for (int i = 4; i < 24; i++) jitter.Push((ushort)i, frames[i], 0, frames[i].Length, 0.6);
            jitter.Pull(decoder, output);
            Assert.Greater(jitter.CatchUps, 0);
            Assert.LessOrEqual(jitter.Count, jitter.TargetDelayFrames);

            // Bounded: a flood of frames with nobody pulling keeps at most Capacity.
            var flood = new VoiceJitterBuffer();
            for (int i = 0; i < 5000; i++) flood.Push((ushort)i, frames[i % 200], 0, VoiceFormat.FrameBytes, i * 0.02);
            Assert.LessOrEqual(flood.Count, VoiceJitterBuffer.Capacity);

            // A new talk spurt long after the last one starts cleanly even though its numbers are far ahead.
            var spurts = new VoiceJitterBuffer();
            for (int i = 0; i < 6; i++) spurts.Push((ushort)i, frames[i], 0, VoiceFormat.FrameBytes, i * 0.02);
            for (int i = 0; i < 40; i++) spurts.Pull(decoder, output);
            for (int i = 0; i < 6; i++) spurts.Push((ushort)(3000 + i), frames[i], 0, VoiceFormat.FrameBytes, 60 + i * 0.02);
            int played = 0;
            for (int i = 0; i < 20; i++) if (spurts.Pull(decoder, output) == VoicePull.Played) played++;
            Assert.AreEqual(6, played);
            Assert.AreEqual(2, spurts.Spurts);
        }

        // ------------------------------------------------------------------ ring and rolloff

        [Test]
        public void Ring_PassesSamplesThrough_WrapsAround_DropsWhenFull_AndCountsUnderruns()
        {
            var ring = new VoiceRing(100);
            Assert.AreEqual(128, ring.Capacity);
            var src = new float[100];
            var dst = new float[100];
            for (int round = 0; round < 10; round++)
            {
                for (int i = 0; i < src.Length; i++) src[i] = round * 1000 + i;
                Assert.AreEqual(100, ring.Write(src, 0, 100));
                Assert.AreEqual(100, ring.Read(dst, 0, 100));
                CollectionAssert.AreEqual(src, dst);
            }
            Assert.AreEqual(100, ring.Write(src, 0, 100));
            Assert.AreEqual(28, ring.Write(src, 0, 100), "full: the rest is dropped");
            Assert.AreEqual(72, ring.Dropped);
            Assert.AreEqual(100, ring.Read(dst, 0, 100));
            Assert.AreEqual(28, ring.Read(dst, 0, 100));
            Assert.AreEqual(1, ring.Underruns);
            Assert.AreEqual(0, ring.Count);
        }

        [Test]
        public void Rolloff_IsClearUpClose_QuietAt10m_AndSilentFrom18m()
        {
            Assert.AreEqual(1f, VoiceRolloff.Gain(0f));
            Assert.AreEqual(1f, VoiceRolloff.Gain(2.5f));
            Assert.Greater(VoiceRolloff.Gain(3f), 0.75f, "a normal conversation distance is clear");
            Assert.That(VoiceRolloff.Gain(10f), Is.InRange(0.12f, 0.22f), "quiet by 10 m (about -14 dB)");
            Assert.Less(VoiceRolloff.Gain(15f), 0.1f);
            Assert.AreEqual(0f, VoiceRolloff.Gain(18f));
            Assert.AreEqual(0f, VoiceRolloff.Gain(40f));
            float previous = 2f;
            for (float d = 0f; d <= 20f; d += 0.1f)
            {
                float g = VoiceRolloff.Gain(d);
                Assert.LessOrEqual(g, previous + 1e-6f, "never louder further away (" + d + " m)");
                previous = g;
            }
        }
    }
}
