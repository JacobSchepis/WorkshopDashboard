# Architecture decision records

Short records of the decisions that shaped the app — the context at the time, what was
decided, and what it cost.

| # | Decision |
| --- | --- |
| [0001](0001-integrate-through-the-jms-web-front-end.md) | Integrate with JMS through its own web front end (SQL export page for reads, form posts for writes) |
| [0002](0002-shared-read-session-per-user-writes.md) | Read through a shared service account with a last-good cache; write as the logged-in user |
| [0003](0003-queue-writes-in-an-outbox.md) | Queue JMS writes in a database outbox when JMS can't take them |
| [0004](0004-llm-proposes-code-decides.md) | The LLM proposes, the code decides |
| [0005](0005-demo-mode-at-the-http-transport.md) | Fake JMS at the HTTP transport for demo mode |
