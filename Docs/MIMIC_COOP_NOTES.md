# The Mimic in co-op

The Mimic (the spider that says your own words back to you) can now be the creature in a co-op contract,
chosen at random like the other three species. Before this change it was never picked in a crew.

## Privacy: what happens to your voice (text for the README)

The following replaces the README's statements that voice chat is "not recorded or stored" and that "Phrases
recorded for the Mimic are never sent", which are no longer exactly true in a crew with the Mimic aboard.

> **The Mimic in a crew.** In a co-op contract with the Mimic aboard, the host's headset keeps short snippets of
> the crew's voice chat in its memory: at most the last four things each crew member said, up to four seconds
> each. The host's own snippets come from the host's microphone, exactly as in single-player. The Mimic may play
> any of these snippets back, lowered, slowed and wobbled, from where the spider is: every headset in the crew
> hears that playback (it travels to them like voice chat, through the host and, online, Unity's Relay). The
> snippets themselves are never sent anywhere, never written to storage and never analysed for words. They are
> erased when the contract ends, when you leave the crew or the connection is lost, when the crew member they
> belong to leaves, and when that crew member switches **MIC** or **VOICE CHAT** off. With **MIC off** or
> **VOICE CHAT off** your voice is not sent to the crew, so nothing of it is kept or played back. (The host
> player's own microphone follows the single-player rule: it is used for the Mimic only with **MIC on**, whatever
> VOICE CHAT says.) While this may be happening the wrist display shows **REC**, on the host and on every crew
> member whose voice chat is on. With VOICE CHAT off you also do not hear the Mimic play back the voices of
> other crew members.

Lines of the README that this makes out of date (README.md is not edited by this change):

- "There is one spider for the whole crew (never the Mimic in co-op)": the Mimic can now be that spider.
- Voice chat "is **not recorded or stored** anywhere, on either end ... Phrases recorded for the Mimic are never
  sent": see the text above.
- "It never sends Mimic recordings": the snippets are never sent, but the Mimic's altered playback is.

The CO-OP CREW panel's crew-mode text now ends "Never saved, but a Mimic may echo it back." instead of
"it is never recorded."

## How it works

**Pick.** `MissionDirector.PickCreature` picks among all species in a session exactly as offline (same seeded
draw); `ApplyCoopRunLimits` no longer swaps the Mimic out. Conditions are unchanged (none of them filtered the
species).

**Whose voice (host only).** `MimicCrewVoice` (Scripts/Runtime/Net/Voice), owned by `VoiceChat`, is the host
creature's `VoiceMimic.ICrewVoice`.

- While a Mimic is aboard and asking (it calls `RequestCapture` every frame while it runs, dormant included;
  capture lapses 0.5 s after it stops asking), every voice-chat frame the host receives from a client is also
  pushed into that client's own jitter buffer and decoder in a `CrewPhraseBank` (Scripts/Core/Voice), pulled in
  real time (silence included, so phrases end when the talker stops) into a `PhraseRecorder` per client:
  **4 phrases per crew member, at most 4 s each**, oldest dropped first (the single-player numbers).
- The host player's phrases are the host's own `VoiceSensor.Recorder` (MIC on; single-player rules), attached to
  the bank, never cleared by it.
- Forgotten: when no Mimic is asking (contract over, another species, hub), on LEAVE / link loss / session closed
  (the bank dies with the VoiceChat binding), when that client is no longer connected, and when that client sends
  the **voice off** notice.
- **Voice off notice.** A client whose MIC or VOICE CHAT goes from on to off sends a one-byte Voice frame
  (`VoiceMsg.OffNotice`) three times, 0.1 s apart (unreliable stream; three tries). It carries no audio; every
  receiver drops it from playback; the host forgets that player's phrases (and stops the Mimic mid-sentence if it
  was saying one of them). Nothing is sent while voice stays on or stays off.
- A client with MIC off or VOICE CHAT off sends no frames, so it is never recorded.
- Frames received while no Mimic is asking are not even decoded for it.

**Triggers and pacing.** Unchanged from single-player (`VoiceMimic.CrewUpdate` mirrors `Update`): a new phrase
from anybody is answered after 1.2 to 2.8 s; with nothing new it repeats an older one every 11 to 22 s; never
while dormant, darted, in a blackout or during a jumpscare; a blackout, a dart, or the contract ending stops it
mid-phrase.

**Who is preferred.** The creature's quarry (`EnemyDrone.Quarry`, the player it is currently stalking) at the
moment it speaks. A crew member is preferred when they are not that player and are more than **8 m** from that
player's head. If any preferred crew member has a phrase, only preferred phrases are used: for an answer, the
newest phrase among preferred speakers; for a repeat, a random phrase among theirs. Otherwise anybody's (the
single-player behaviour). With no quarry, no preference.

**Playback everyone hears.** The chosen phrase is wobbled (the same formula as single-player, now in
`MimicVoiceRender.Warble`), then rendered at the Mimic's pitch (0.86: lower and slower, as the AudioSource pitch
did) at the 16 kHz voice rate (`MimicVoiceRender.AtPitch`), encoded with the unchanged voice codec and streamed in
real time by `CreatureVoiceSender` (two frames per message, 40 ms apart, two frames' lead) with
`NetSessionHost.SendCreatureVoice` in the existing Voice stream message with the pseudo-sender slot **7**
(`VoiceMsg.CreatureSlot`). Clients decode it through a jitter buffer and play it from a `VoicePlayback` made with
`VoicePlayback.CreateCreature` on the replica's Mimic speaker: fully 3D, the Mimic speaker's own linear rolloff
(2.5 to 26 m), the Mimic's volume (0.85), wall occlusion like crew voices, no talk light. The host loops the same
encoded frames into the same kind of source on its own creature (so the host hears exactly what the crew hears,
codec included). In co-op the Mimic's offline AudioSource clip path is not used on the host, and a client's
replica Mimic never listens or speaks by itself.

**Slot 7 rules.** `VoiceMsg.Read` accepts slots 0 to 3 and 7 only. The host refuses a client's message written
with slot 7 (`HandleVoice`, counted as rejected, not relayed); any other client slot is overwritten with the
sender's real slot as before. The host's creature voice is not rate-limited (it is paced in real time by the host;
at most 50 frames a second while the creature talks). A client hears slot 7 only from the host (a client session
only ever receives from the host).

**Echo and the spider's ears.** On every headset (host and clients), while the creature's voice is audible at the
listener, the microphone is suppressed (`VoiceSensor.Suppress`, held 0.4 s past the last audible frame): nothing is
sent as voice chat and nothing counts as that player's noise for the spider. The host also suppresses for the whole
phrase when it starts (as single-player does). The creature voice level at the listener is also given to the echo
suppressor along with crew voices.

**Comfort.** No new jumpscare or cue. The playback is never louder than the single-player Mimic: same peak-levelled
phrase, same wobble, volume 0.85, same rolloff, plus wall occlusion that can only lower it. A headset with VOICE
CHAT off does not play the creature's copy of other players' voices (the host with VOICE CHAT off still hears the
Mimic say the host's own words).

## Protocol

`NetProtocol.Version` is now **5** (slot 7 in the Voice message and the one-byte notice are new meanings on the
wire; an older build would drop them as malformed and its player would never hear the Mimic). If another branch
also bumps the version, the merged build needs one number above both.

## Not verified on hardware

Nobody has heard this on two headsets: how the slowed codec voice sounds compared to the offline clip, whether the
creature voice is clear enough at range over Relay jitter, whether 0.4 s of suppression covers the real speaker
latency and room tail of a Quest, and whether the speakers-to-microphone leak of a loud Mimic phrase is fully
caught on a real headset.
