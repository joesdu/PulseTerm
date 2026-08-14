# 11 · Automation and AI

Both are "application layers above the plugin system" and share the same
principle: **the host provides the skeleton and security boundaries, while
plugins provide the substance**.

## 1. Automation (S5/S6)

### 1.1 Model: Rule = Trigger + Condition + Action

```text
Trigger           session connected / transfer completed / scheduled (cron) / manual command / plugin-defined trigger
Condition         when expression (reuse 03 §6 grammar): host matching, time window, previous result…
Action            built-in actions (execute remote command, upload/download, send notification, write to terminal…)/ plugin-defined action
```

The **host** owns the rule itself (storage, scheduling, execution orchestration,
and logs). Plugins participate in two deliberately separate ways:

1. **A plugin consumes host events and does its own work** (07 §8's
   `vela.events` + capabilities): suitable for complex logic, with every action
   covered by that plugin's permissions and audit trail.
2. **A plugin contributes triggers/actions to the rule engine** (manifest
   `contributes.automation`): users visually compose them on the host's
   "Automation" page and use plugin capabilities as building blocks. During
   execution, the host calls the plugin back (`automation/evaluateTrigger`,
   `automation/runAction`); action execution remains subject to the plugin's
   own permissions, so the rule engine does not amplify permissions.

### 1.2 Security Boundaries

- Rules can be created or modified only by the **user in the host UI**. A plugin
  can only "suggest" a rule (`SuggestRuleAsync`, which displays a confirmation
  card), never create one silently. The combination of automation and
  `remote.exec` is powerful enough that a human must remain in the loop.
- Each rule displays an aggregate of the permissions it will use. Rules
  containing highly sensitive actions (`remote.exec`/`terminal.write`) require
  item-by-item confirmation before their first run, with an option to
  "automatically run this rule in the future."
- Rule execution history (time, trigger reason, action-output summary, and
  success or failure) is persisted; failures can be rerun with one click. After
  N consecutive failures, the rule is automatically suspended and a notification
  is sent.
- Plugins awakened by cron triggers use the `onSchedule` activation event and
  are subject to load staggering and a minimum interval of 1 minute, preventing
  a plugin from turning the host into a request blaster.

### 1.3 Built-In Actions (Provided by the Host, No Plugin Required)

Execute remote commands (exec channel), upload/download files, send
notifications, open sessions, write to the terminal (highly sensitive), and run
local programs (highly sensitive, explicit path). This makes lightweight
automation available without plugins, while plugins handle the long tail.

## 2. AI Gateway (S3, Longer-Term Mainline)

### 2.1 Why a "Gateway" Instead of Giving Every Plugin Its Own Model SDK

- **The host owns the keys**: the user configures model providers once in the
  host (Anthropic/OpenAI/local Ollama…), and API keys are stored in the OS
  credential store. Plugins never receive them.
- **Quotas and auditing**: track token usage and limits by plugin, and audit
  "which plugin sent what content to the model." This is critical for
  operations tools because terminal content may contain secrets.
- **One integration, available everywhere**: the host absorbs model-provider
  changes, while plugins use a stable abstraction.

### 2.2 Interface Shape (`vela.ai`, Permission `ai.invoke`)

```csharp
public interface IAi
{
    Task<AiModelInfo[]> ListModelsAsync(CancellationToken ct);       // Available configured tiers (such as fast/balanced/powerful; keys are not exposed)
    IAsyncEnumerable<AiDelta> ChatStreamAsync(AiRequest req, CancellationToken ct);
        // AiRequest: tier, messages, optional tools (plugin-provided tool callbacks are executed by calling the plugin back through RPC)
    Task<float[][]> EmbedAsync(string model, string[] inputs, CancellationToken ct);
}
```

- Tool-calling loop: the model requests a tool → the host calls the plugin's
  tool implementation through RPC → the result is returned to the model. Tool
  execution remains subject to the plugin's permissions; AI is not a permission
  bypass.
- **Data-egress notice**: the first time a plugin sends content obtained through
  `terminal.read` to `ChatStream`, the authorization prompt explicitly says
  "Terminal content will be sent to <model provider>"; Settings provides a
  master switch to "Prevent all plugins from sending terminal content to AI."
- The host implementation should use the official SDK for each provider;
  local models connect through an OpenAI-compatible endpoint and are routed
  uniformly inside the gateway.

### 2.3 Convergence with Automation

An AI action ("summarize this deployment output and notify me") is a rule-engine
action that calls `ai.invoke`; an AI trigger (semantic detection of anomalous
logs) is a plugin subscribing to the terminal stream, asking the gateway for a
determination, and triggering a rule. Combining the two skeletons produces an
"intelligent operations" scenario without introducing a new mechanism.

## 3. Development Plan (This Area)

Automation (starts after milestone M3):

| Task | Description | Dependencies | Estimate |
| --- | --- | --- | --- |
| T-1 | Rule model + storage + scheduler (cron/event bridging), first batch of built-in actions | C-3, C-5 | 5d |
| T-2 | Automation management page (rule list/editor/execution history) | T-1 | 5d |
| T-3 | Plugin trigger/action contributions + callback protocol + SuggestRule confirmation flow | T-1, M-4 | 3d |
| T-4 | Highly sensitive rule confirmation flow + failure suspension + audit integration | T-2, B-6 | 2d |
| T-5 | Official auto-runner sample (supports S-9) | T-3 | — |

AI gateway (independent track, can run in parallel with M6+):

| Task | Description | Dependencies | Estimate |
| --- | --- | --- | --- |
| I-1 | Gateway abstraction + two provider implementations (Anthropic + OpenAI-compatible endpoint) + key-management settings page | — | 5d |
| I-2 | `vela.ai` capability domain (streaming, tool loop, per-plugin metering and limits) | I-1, C-1 | 4d |
| I-3 | Data-egress safeguards (terminal-content marking and master switch, auditing) | I-2, B-6 | 2d |
| I-4 | Official AI assistant sample plugin (S3: explain errors/generate and send back commands) | I-2, C-5 | 4d |

Acceptance: S5, complete the fully visual orchestration of "automatically run an
inspection script and send me a notification when a `prod-*` session connects";
S3, after authorization, the AI sample plugin can explain a selected terminal
error, and the audit page shows a summary of the content sent.
