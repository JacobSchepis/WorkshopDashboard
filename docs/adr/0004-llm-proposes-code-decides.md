# 4. The LLM proposes, the code decides

**Status:** Accepted

## Context

Quoting was the slowest routine job in the workshop: read a repairer's PDF (six suppliers,
six layouts), work out what repair it describes, apply the customer's pricing group, convert
currency, add markup, round to the house style and type it into JMS. In-house quotes meant
matching a technician's free-text findings to lines on a long price sheet.

LLMs are good at the reading and bad at the parts with consequences: they invent plausible
codes, do arithmetic unreliably, and give different answers to the same input. A wrong price
on a customer quote costs real money or a customer.

## Decision

Use the model only to turn unstructured text into a structured proposal, and keep every
decision with a consequence in ordinary, tested code:

- **Structured output only.** Every call uses a strict JSON schema; enums constrain
  categorical fields (a Dorado repair level is one of five values, including "none").
- **Validate against the source of truth.** Codes the model returns are looked up on the
  price sheet; anything not on it is dropped and reported. Descriptions and prices always
  come from the sheet.
- **No arithmetic in the model.** Pricing (groups, FX, markup, discount tiers, caps,
  rounding) is C# with unit tests. When the repair suggester's second pass proposes combining
  lines, the code checks that the result is actually cheaper before accepting it.
- **Prefer an empty answer to a guess.** Prompts say so explicitly for the known failure
  modes, and the code treats "not present" as a real case with its own pricing path.
- **Repeatable where possible.** Temperature 0 when the model accepts it; the result records
  whether it did.
- **A person submits.** Nothing the model produces reaches JMS without someone reviewing it on
  the page.

The details are in [the LLM pipeline write-up](../llm-pipeline.md).

## Consequences

- The model can be wrong in only a few, visible ways: a field misread (which the operator
  sees next to the PDF) or a finding mapped to the wrong real line (shown with its reason and
  confidence). It can't produce a price that doesn't follow the rules or a code that doesn't
  exist.
- Pricing rules are testable and were tested; changing a supplier's markup is a code change
  with a test, not a prompt edit.
- Each supplier needs its own schema and prompt, and a supplier changing their quote layout
  needs a prompt revisit. There's no evaluation set yet, so that revisit is manual — the
  write-up lists that as the next thing to build.
- Swapping models or providers touches one interface (`IJsonCompletionClient`), which is also
  what made a key-free demo possible.
