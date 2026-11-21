# 协作任务的模型选择

在 App 中打开「协作 → 协作任务 → 分工协作」，向下滚动到「领队」即可选择工具、模型和思考深度。

领队负责拆分任务、协调修改范围和验收结果。给执行成员选模型时，先看任务难度、当前账号可用的模型和额度，再考虑速度和成本。同一种工具可以开多段独立对话，使用同一账号时，这些对话共用账号额度。

| 工具与模型 | 适合安排的工作 |
| --- | --- |
| Codex · GPT-6.2 Sol | 日常编码、写作、多来源整理。可用于常规领队工作、小范围实现和测试。 |
| Codex · GPT-6.2 Astra | 跨模块排查、难以定位的错误、复杂设计取舍，或需要综合多种证据的验收。 |
| Codex · GPT-6.1 Terra | 范围清楚、需要快速反复尝试的改动，例如补测试或核对一批明确的调用点。 |
| Claude Code · Sonnet | 在速度和能力之间取平衡。可用于实现、重构和文档撰写。 |
| Claude Code · Opus | 需要长时间推理或处理复杂代码的任务，例如梳理有多处依赖的设计、审查难定位的问题。 |
| Claude Code · Haiku | 需要低延迟或高频处理的简短任务，例如分类、提取信息和整理检查清单。 |
| Antigravity CLI · Gemini 4 Carbon、Neon | 适合复杂代码、跨文件分析和多模态材料。Carbon 侧重深度推理，Neon 适合从分析做到实现和验证。 |
| Antigravity CLI · Gemini 4 Argon | 适合仍需推理、同时关注响应时间和用量的编码或审查。 |
| Antigravity CLI · Gemini 4 Hydrogen | 适合日常编码、界面细节和范围明确的批量检查。 |
| Gemini Pro、Flash（工具列出时） | Pro 可用于复杂推理和材料综合，Flash 可用于低延迟的常规分析和提取。 |

模型名称和选项以电脑上的工具实际返回为准。上表是选型参考，并不表示某个账号能使用表里的全部模型。

## 怎样选思考深度

- 常规领队工作可从 **Sol + 中等（medium）** 开始。只需拆分几个互不影响的任务、收集结果并检查测试时，通常用不到更高档位。
- 跨模块排查、有多个合理方案需要判断，或验收失败原因不清楚时，可选择 **Astra + 高（high）**。提高思考深度通常会增加响应时间和额度消耗。
- 执行路径和验收标准已经明确时，可以把任务交给响应更快的模型。需要独立复核时，把具体检查项交给另一个可用成员，最终由领队核对实际改动和检查结果。

选型依据见各厂商的模型说明。[OpenAI Codex](https://developers.openai.com/codex/models/) · [Anthropic Claude](https://platform.claude.com/docs/en/about-claude/models/overview) · [Google Gemini](https://ai.google.dev/gemini-api/docs/models)
