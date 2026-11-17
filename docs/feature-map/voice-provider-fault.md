Section: Talking and typing

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| voice-provider-fault | The live voice check **names a provider fault**: when OpenAI's own transcript gives the right answer but every answer audio sample stays below 50 of 32767 (digital silence), the failure message starts with "PROVIDER FAULT". It is still a failure: the verdict passes exactly what the 0.15 s spoken-answer rule passes. The question asks for a short sentence ("The answer is five.") so a correct answer carries more speech | automated mock | `tools/Boo.LiveSmoke/SpokenAudioTests.cs::Silent_provider_audio_with_the_right_transcript_is_named_a_provider_fault_and_still_fails`; `tools/Boo.LiveSmoke/SpokenAudioTests.cs::Silence_is_not_named_a_provider_fault_when_the_transcript_is_wrong_or_the_audio_is_only_quiet`; `tools/Boo.LiveSmoke/SpokenAudioTests.cs::The_verdict_passes_exactly_what_the_spoken_answer_rule_passes` (workflow `live-ai-smoke`); issue #177 |
