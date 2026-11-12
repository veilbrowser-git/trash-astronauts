# Proximity voice chat (notes for the README)

## For players

In a co-op game you can now talk to your crew. A crew member's voice comes from their helmet: clear when
they are next to you (within about 2.5 m), quieter as they move away (about a fifth as loud at 10 m), silent
beyond 18 m, and muffled and quieter when a wall is between you. It goes through the same helmet filter as
every other sound, so it sounds a bit like a suit radio. A crew member who is talking shows as talking to the
game (`VoiceChat.IsSpeaking`); nothing in the suit or on the wrist shows it yet.

**VOICE CHAT** in the suit settings (the wrist menu and the console in the tug) switches it on or off. It is
on by default. With it off you send nothing and hear nobody. **MIC** still decides whether the game uses your
microphone at all: with MIC off nothing is captured, so nothing is sent (you can still hear your crew).

## What is sent, and where

This changes an earlier promise. Before this version no microphone audio was ever sent anywhere. Now:

- **What:** your own voice, compressed, and only while you are talking. When you are quiet nothing is sent.
  Phrases the Mimic recorded are never sent. Nothing is written to storage on either end.
- **To whom:** only the other players in your crew (the co-op game you joined or are hosting). It goes through
  the host's headset, which passes it to the others. In an online game it also passes through Unity's Relay
  servers, like every other co-op message. It is not sent to anyone outside the crew.
- **How much:** about 6.2 kB of audio per second of talking (6.75 kB/s with the game's packet headers, about
  7.5 kB/s counting UDP/IP headers): 16 kHz capture, 12 kHz IMA ADPCM at 4 bits per sample, 20 ms frames, two
  frames per packet, 25 packets a second while talking. The host forwards each talker to each other player, so a
  host with three talking friends uploads about three times that.
- **When not:** in single-player nothing of voice chat exists. With VOICE CHAT off, MIC off, or while the
  Mimic is talking through your speakers, nothing is sent.

## Known limits (honest)

- **Untested on a headset.** Everything was checked by automated tests with synthetic voices and no real
  microphone. How it sounds on a Quest, how loud it is, its delay, and whether the echo suppression copes with
  the Quest's own speakers are all unconfirmed.
- **Echo suppression is basic.** Your microphone hears your friends' voices from your headset speakers. While a
  crew voice is playing (and for 0.35 s after), your microphone only counts as you when it is clearly louder
  than what the speakers explain; anything quieter is not sent and is not heard by the creature as you. This
  stops feedback and echo, but it is closer to a walkie-talkie than a phone: if you talk quietly while a friend
  is talking, the start of what you say can be cut until they stop. It learns how loud your speakers are at the
  microphone over time, starting from a cautious guess.
- **Quality is telephone-like.** 12 kHz ADPCM is clearly intelligible (about 21 dB signal-to-noise on test
  speech) but not hi-fi; the helmet filter takes off the top end anyway.
- **Delay:** about 80 to 120 ms of buffering (it adapts to the network's jitter), plus the network and the
  headset's own audio latency. Lost packets are covered by repeating the last 20 ms at falling volume.
- **Background noise:** a steady noise (a fan) is learned and ignored within about two seconds; noise that
  changes a lot can open the voice gate and be sent.
- The creature still hears only your real voice through your own microphone, as before; it does not hear your
  crew's voices.

## Changes needed in README.md (not made here)

README.md is being edited elsewhere. These sentences there are no longer true and should be replaced with the
text above: "nothing is recorded or sent anywhere" (0.5.0 notes), "No microphone audio is sent, online or on
Wi-Fi" (known limits), "No microphone audio and no recorded ..." and "never leaves the headset" (privacy
section), and "never microphone audio" (online section). The NetSession class comment ("Nothing here reads the
microphone, and no message type can carry audio") needs the same update. Test counts change too (new
VoiceCodecTests, NetVoiceTests and VoiceChatPlayTests).
