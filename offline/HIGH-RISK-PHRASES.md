# Local English phrase repairs

Some exact English patterns receive structured Simplified Chinese output before the translation model runs. This keeps the relationship, deadline boundary and currency symbol explicit in the reviewed constructions. Non-matching sentences continue through the Argos model. These rules are intentionally narrow: they are not a general grammar parser or a guarantee of correct translation. Check every important result against its original English text.

Supported construction families:

- “meeting was/has been moved from [weekday] to [weekday] at [time]” keeps original date, replacement date and time separate.
- “submit/file/send [report, form, application, payment, invoice or file] by [weekday]” renders the named weekday as the included deadline.
- “changes made before [time] today will be reviewed tomorrow” keeps cutoff and review day separate.
- “reduce [total, amount, price, budget or error rate] by X%, not to Y%” explicitly distinguishes the percentage decrease from the resulting value.
- “refund will not/won't be issued until N days after the returned [item, product or package] is received” anchors the timer to receipt of the returned goods.
- “trial lasts for N days/weeks/months after installation” uses the software-trial meaning.
- “only delete files newer than N days” is expressed as files within the recent N-day window.
- “do not start the next step until the previous step has finished” keeps the prohibition in force before completion.
- “start the next step only after the previous step has finished” keeps “only after” as a condition, not an affirmative instruction to start.
- If inference is used for text that contains a currency symbol, known translated currency labels are replaced with the original symbol and digits.

Each supported pattern has positive, variant and negative tests in `src/OfflineTranslationAcceptance.cs`. Matching requires the whole recognized line; unrelated sentences fall back to the model.
