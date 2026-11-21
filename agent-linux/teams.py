"""Work several AI conversations do together on this machine (agent-windows/Teams.cs).

A team is one of three ways of working: `review` (one AI writes, another reviews read-only),
`lead` (a leader assigns members by ASSIGN lines and collects the results), and `compare`
(candidates each work in their own Git worktree and the owner merges one). The members are
conversations this agent begins and tells; the orchestration below is a port of Teams.cs, and
what it needs from this computer is the `Hands` set of callables — on Windows they run the
tools' structured chats; here a member is a structured chat where the tool has one (Claude
Code, Codex) and a phone terminal otherwise, whose turn is known to have ended when its
conversation record has gone quiet after growing (the way chains knows, agent.py).

The state of every team is kept in one JSON file (0600) next to the other stores.
"""
import json
import os
import re
import subprocess
import threading
import time

MOST = 10               # teams kept at all (ended ones are dropped oldest first)
MOST_ROUNDS = 10        # the most rounds a new team may ask for
MAX_TOTAL_ROUNDS = 20   # the most rounds a team can reach by being continued
CHAIN_QUIET_DEFAULT = 45.0

MODES = ("review", "lead", "compare")
TOOL_LABELS = {"claude": "Claude Code", "codex": "Codex", "opencode": "OpenCode", "qoder": "Qoder",
               "cline": "Cline", "pi": "Pi", "agy": "Antigravity CLI", "shell": "终端"}
STRENGTHS = {
    "codex": "擅长决策拍板、任务拆解、精准改代码与修测试（优先负责决策，纯检查不必浪费其额度）",
    "claude": "擅长主力写作（代码实现、重构与文档撰写）以及深度交叉审查",
    "agy": "Gemini 模型，擅长 UI 与前端界面设计、长上下文检索，也可兼顾写作与交叉检查",
    "opencode": "终端里的开源编码代理，擅长直接改代码、跑命令与多模型切换",
    "qoder": "擅长任务拆解、多文件重构与按规范推进",
    "cline": "擅长逐步执行与编辑器联动，适合小步验证的改动",
    "pi": "轻量快速，适合小步修改、速查与交叉检查"}

QUOTA_ERROR = re.compile(r"(hit your limit|rate[\s_-]*limit|usage[\s_-]*limit|quota|out of credits|credit balance"
                         r"|too many requests|\b429\b|overloaded|额度|限额|已达上限|额度用尽|额度已用完|额度用完)", re.IGNORECASE)
ASSIGN = re.compile(r"^[\s>*\-_`]*ASSIGN[*_`]*\s+[*_`]*([a-z][a-z0-9_-]*)[*_`]*\s*[:：]\s*(.+?)\s*$",
                    re.MULTILINE | re.IGNORECASE)
DONE = re.compile(r"^[\s>*\-_`]*DONE[*_`]*\s*[:：]\s*(.*)$", re.MULTILINE | re.IGNORECASE)
ID_OK = re.compile(r"\A[a-f0-9]{16,32}\Z")

REMIND = ("【协作任务】没有在你的回答里找到分派或结束。请在回答的最后按格式写：\n"
          "ASSIGN 成员名: 任务内容（可以多行）\n或单独一行：\nDONE: 总结")


def cut(text, most):
    text = text or ""
    return text if len(text) <= most else text[:most] + "\n…（后面省略）"


def now_ms():
    return int(time.time() * 1000)


def is_quota_error(text):
    return bool(text and text.strip()) and bool(QUOTA_ERROR.search(text))


def verdict(review):
    """A review's verdict from its first lines: True pass, False changes, None none."""
    lines = [one.strip() for one in (review or "").split("\n") if one.strip()][:3]
    for line in lines:
        plain = re.sub(r"[*_`#>\s]", "", line).upper()
        if plain.startswith("REVIEW:PASS") or plain.startswith("REVIEW：PASS"):
            return True
        if plain.startswith("REVIEW:CHANGES") or plain.startswith("REVIEW：CHANGES"):
            return False
    return None


def label(tool):
    return TOOL_LABELS.get(tool, tool)


def changes_in(folder):
    """What changed in a project's folder, as git says it: the files, and the changes themselves
    (a folder that is no repository says nothing)."""
    if not folder or not os.path.isdir(folder):
        return ""
    try:
        files = subprocess.run(["git", "-C", folder, "status", "--porcelain", "--untracked-files=all"],
                               capture_output=True, timeout=15)
        if files.returncode != 0 or not files.stdout.strip():
            return ""
        diff = subprocess.run(["git", "-C", folder, "-c", "core.quotepath=false", "diff", "HEAD", "--no-color"],
                              capture_output=True, timeout=15)
        return "文件：\n" + files.stdout.decode("utf-8", "replace").rstrip() + "\n\n" + \
            (diff.stdout.decode("utf-8", "replace").rstrip() if diff.returncode == 0 else "")
    except (OSError, subprocess.TimeoutExpired):
        return ""


def failure_reason(said):
    if is_quota_error(said):
        return "额度已用尽"
    one_line = re.sub(r"\s+", " ", (said or "").strip())
    return cut(one_line, 140) if one_line else "执行出错"


class Hands:
    """What this computer does for a team: begin a member's conversation (optionally in a
    worktree folder), tell it something, stop a turn, read project changes, read quota
    windows, list the chat-capable tools, and manage Git worktrees for `compare`. Every
    member may be None where this computer cannot do that thing (the ported ways check)."""

    def __init__(self, begin=None, begin_in=None, say=None, stop=None, changes=None, limits=None,
                 available=None, make_worktree=None, worktree_diff=None, worktree_stat=None,
                 apply_worktree=None, clean_worktrees=None):
        self.begin, self.begin_in, self.say, self.stop = begin, begin_in, say, stop
        self.changes, self.limits, self.available = changes, limits, available
        self.make_worktree, self.worktree_diff, self.worktree_stat = make_worktree, worktree_diff, worktree_stat
        self.apply_worktree, self.clean_worktrees = apply_worktree, clean_worktrees


class Team:
    def __init__(self):
        self.id = self.dir = self.mode = self.goal = self.rules = ""
        self.state = "running"
        self.note = self.phase = self.picked = ""
        self.discuss = False
        self.reminded = False
        self.tools = []
        self.rounds, self.round = 3, 1
        self.created = 0
        self.log = []               # [{"at": ms, "who": str, "text": str}]
        self.members = {}           # role -> terminal id
        self.awaited = []           # roles whose turn is awaited
        self.answers = {}           # role -> what it said this round
        self.worktrees = {}         # tool -> path (compare)
        self.base_commits = {}      # tool -> base commit sha
        self.summaries = self.stats = self.diffs = {}
        self.failed = {}            # tool -> brief reason
        self.pending = {}           # role -> the prompt last dispatched

    def copy(self):
        other = Team()
        for key, value in vars(self).items():
            if isinstance(value, list):
                setattr(other, key, list(value))
            elif isinstance(value, dict):
                setattr(other, key, dict(value))
            else:
                setattr(other, key, value)
        return other

    def to_json(self):
        return {"id": self.id, "dir": self.dir, "mode": self.mode, "goal": self.goal, "rules": self.rules,
                "state": self.state, "note": self.note, "phase": self.phase, "picked": self.picked,
                "discuss": self.discuss, "reminded": self.reminded,
                "tools": self.tools, "rounds": self.rounds, "round": self.round,
                "created": self.created, "members": self.members, "awaited": self.awaited,
                "answers": self.answers, "worktrees": self.worktrees, "base_commits": self.base_commits,
                "summaries": self.summaries, "stats": self.stats, "diffs": self.diffs,
                "failed": self.failed, "pending": self.pending,
                "log": [{"at": s["at"], "who": s["who"], "text": s["text"]} for s in self.log]}

    @staticmethod
    def from_json(kept):
        if not isinstance(kept, dict):
            return None
        word = lambda name: str(kept.get(name) or "")
        team = Team()
        team.id, team.dir, team.mode = word("id"), word("dir"), word("mode")
        team.goal, team.rules, team.state = word("goal"), word("rules"), word("state") or "running"
        team.note, team.phase, team.picked = word("note"), word("phase"), word("picked")
        team.discuss = word("discuss").lower() == "true"
        team.reminded = word("reminded").lower() == "true"
        team.tools = [str(one) for one in kept.get("tools") or [] if isinstance(one, str)]
        team.members = {str(k): str(v) for k, v in (kept.get("members") or {}).items()}
        team.awaited = [str(one) for one in kept.get("awaited") or []]
        team.answers = {str(k): str(v) for k, v in (kept.get("answers") or {}).items()}
        team.worktrees = {str(k): str(v) for k, v in (kept.get("worktrees") or {}).items()}
        team.base_commits = {str(k): str(v) for k, v in (kept.get("base_commits") or {}).items()}
        team.summaries = {str(k): str(v) for k, v in (kept.get("summaries") or {}).items()}
        team.stats = {str(k): str(v) for k, v in (kept.get("stats") or {}).items()}
        team.diffs = {str(k): str(v) for k, v in (kept.get("diffs") or {}).items()}
        team.failed = {str(k): str(v) for k, v in (kept.get("failed") or {}).items()}
        team.pending = {str(k): str(v) for k, v in (kept.get("pending") or {}).items()}
        team.rounds = _int(kept.get("rounds"), 3)
        team.round = _int(kept.get("round"), 1)
        team.created = _int(kept.get("created"), 0)
        for step in kept.get("log") or []:
            if isinstance(step, dict):
                team.log.append({"at": _int(step.get("at"), 0), "who": str(step.get("who") or ""),
                                 "text": str(step.get("text") or "")})
        return team if ID_OK.match(team.id) and team.mode in MODES else None


def _int(value, fallback):
    try:
        return int(value)
    except (TypeError, ValueError):
        return fallback


class OpError(Exception):
    """What the phone is told, in its own words."""


class Teams:
    """The teams of this machine: the port of Teams.cs. `hands` carries this computer's ways;
    `chat_tools` names the tools whose conversations report their own turn endings (the rest
    are polled by the caller through `polled`, below)."""

    def __init__(self, file, hands, chat_tools=(), quiet=None, labels=None):
        self.file = file
        self.hands = hands
        self.chat_tools = tuple(chat_tools)
        self.quiet = CHAIN_QUIET_DEFAULT if quiet is None else quiet
        self.labels = dict(TOOL_LABELS)
        if labels:
            self.labels.update(labels)
        self.gate = threading.Condition(threading.RLock())
        self.opening = 0
        self.items = []
        self.changed = None                 # a callable woken when a team changed
        self.load()

    def label(self, tool):
        return self.labels.get(tool, tool)

    def load(self):
        if not self.file or not os.path.isfile(self.file):
            return
        try:
            with open(self.file, encoding="utf-8") as stream:
                kept = json.load(stream)
            items = [Team.from_json(one) for one in kept if isinstance(one, dict)]
            for team in filter(None, items):
                # its members' turns were cut when this program stopped: it waits for its owner
                if team.state == "running":
                    team.state = "paused"
                    team.note = "电脑端重新启动，成员这一轮被打断：点“继续”接着做"
                self.items.append(team)
        except (OSError, ValueError, TypeError):
            pass

    def save(self):
        if not self.file:
            return
        try:
            os.makedirs(os.path.dirname(self.file), exist_ok=True)
            fresh = self.file + ".new"
            with open(fresh, "w", encoding="utf-8") as stream:
                json.dump([team.to_json() for team in self.items], stream, ensure_ascii=False)
            os.chmod(fresh, 0o600)
            os.replace(fresh, self.file)
            os.chmod(self.file, 0o600)
        except OSError:
            pass

    def tell_changed(self):
        if self.changed:
            try:
                self.changed()
            except Exception:
                pass

    def all(self):
        with self.gate:
            return [team.copy() for team in self.items]

    @staticmethod
    def shown(team):
        shown = {"id": team.id, "dir": team.dir, "mode": team.mode, "goal": team.goal, "rules": team.rules,
                 "state": team.state, "note": team.note, "phase": team.phase, "discuss": team.discuss,
                 "picked": team.picked, "tools": list(team.tools), "rounds": team.rounds, "round": team.round,
                 "created": team.created, "members": dict(team.members), "awaited": list(team.awaited),
                 "failed": dict(team.failed),
                 "log": [{"at": s["at"], "who": s["who"], "text": s["text"]} for s in team.log[-80:]]}
        if team.mode == "compare":
            shown["candidates"] = [{"tool": tool,
                                    "summary": team.summaries.get(tool, ""),
                                    "stat": team.stats.get(tool, ""),
                                    "diff": cut(team.diffs.get(tool, ""), 24000),
                                    "ready": tool in team.worktrees} for tool in team.tools]
        return shown

    def log(self, team, who, text):
        with self.gate:
            team.log.append({"at": now_ms(), "who": who, "text": cut(text, 4000)})
            if len(team.log) > 200:
                del team.log[:len(team.log) - 200]
            self.save()
        self.tell_changed()

    def of(self, id):
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            return team.copy() if team else None

    # ---- what the owner does
    def start(self, given):
        word = lambda name: str(given.get(name) or "")
        tools = [str(one) for one in given.get("tools") or [] if isinstance(one, str)]
        try:
            rounds = int(word("rounds"))
        except ValueError:
            rounds = 0
        rules = word("rules").strip()
        if len(rules) > 1000:
            raise OpError("分工偏好不能超过 1000 字")
        mode = word("mode")
        discuss = mode in ("review", "lead") and word("discuss").lower() == "true"
        team = Team()
        team.id, team.dir, team.mode = word("id"), word("dir"), mode
        team.goal, team.rules = word("goal").strip(), rules
        team.discuss, team.tools = discuss, tools
        team.rounds = max(1, min(MOST_ROUNDS, rounds if rounds else (1 if mode == "compare" else 3)))
        team.created = now_ms()
        if not ID_OK.match(team.id) or team.mode not in MODES:
            raise OpError("协作任务无效")
        if not team.goal or len(team.goal) > 4000:
            raise OpError("目标需为 1 至 4000 字")
        if team.mode == "review" and len(tools) != 2:
            raise OpError("写审需要一个写的 AI 和一个审查的 AI")
        if team.mode == "lead" and (len(tools) < 2 or len(tools) > 4 or len(set(tools[1:])) != len(tools) - 1
                                    or tools[0] in tools[1:]):
            raise OpError("领队需要一个领队和 1 至 3 个不同的成员")
        if team.mode == "compare" and (len(tools) < 2 or len(tools) > 3 or len(set(tools)) != len(tools)):
            raise OpError("并行比稿需要 2 至 3 个不同的 AI")
        if team.mode == "compare" and self.hands.make_worktree is None:
            raise OpError("当前环境不支持独立工作区比稿")

        with self.gate:
            same = next((one for one in self.items if one.id == team.id), None)
            if same is not None:
                return same.copy()
            if sum(1 for one in self.items if one.state in ("running", "paused")) >= 3:
                raise OpError("最多同时进行 3 个协作任务，请先结束一个")
            self.items.append(team)
            while len(self.items) > MOST:
                drop = next((one for one in self.items if one.state not in ("running", "paused", "choose")), self.items[0])
                self.items.remove(drop)
            self.save()

        if team.mode == "compare":
            try:
                for tool in team.tools:
                    created = self.hands.make_worktree(team.dir, team.id, tool)
                    with self.gate:
                        team.worktrees[tool] = created["path"]
                        team.base_commits[tool] = created["base_commit"]
                        team.awaited.append(tool)
                        self.save()
            except Exception:
                if self.hands.clean_worktrees:
                    try:
                        self.hands.clean_worktrees(team.dir, team.id)
                    except Exception:
                        pass
                with self.gate:
                    if team in self.items:
                        self.items.remove(team)
                    self.save()
                raise
            self.log(team, "目标", team.goal)
            for tool in team.tools:
                self.open(team, tool, tool, self.compare_first(team, tool), False, team.worktrees[tool])
            return self.of(team.id)

        self.log(team, "目标", team.goal)
        if team.discuss:
            discussants = team.tools[1:] if team.mode == "lead" else list(dict.fromkeys(team.tools))
            with self.gate:
                team.phase = "discuss"
                for tool in discussants:
                    if "discuss:" + tool not in team.awaited:
                        team.awaited.append("discuss:" + tool)
                self.save()
            for tool in discussants:
                self.open(team, "discuss:" + tool, tool, self.discuss_first(team, tool), True, None)
            return self.of(team.id)

        role = "writer" if team.mode == "review" else "leader"
        first = self.writer_first(team) if team.mode == "review" else self.leader_first(team, self.rooms(team))
        self.open(team, role, team.tools[0], first, False, None)
        return self.of(team.id)

    def stop(self, id):
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            if team is None:
                raise OpError("没有找到这个协作任务")
            busy = [team.members[role] for role in team.awaited if team.members.get(role)]
            team.state = "stopped"
            team.note = "已停止"
            team.awaited.clear()
            self.save()
        for terminal in busy:
            if terminal:
                try:
                    self.hands.stop(terminal)
                except Exception:
                    pass
        self.tell_changed()

    def resume(self, id):
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            if team is None:
                raise OpError("没有找到这个协作任务")
            if team.state != "paused":
                raise OpError("这个协作任务没有暂停")
            team.state = "running"
            team.note = ""
            again = [(role, team.members[role]) for role in team.awaited if role in team.members]
            self.save()
        for role, terminal in again:
            self.deliver_or_reopen(team, role, terminal, "继续完成上面的任务。")
        self.tell_changed()

    def say(self, id, text, target=""):
        text = (text or "").strip()
        target = (target or "").strip()
        if not text or len(text) > 4000:
            raise OpError("消息需为 1 至 4000 字")
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            if team is None:
                raise OpError("没有找到这个协作任务")
            if team.state == "failed":
                raise OpError("这个协作任务没有启动成功，请重新新建")
            if team.mode == "compare" and (team.state == "done" or not team.worktrees):
                raise OpError("比稿方案已结束并清理工作区，请新建协作任务")
            state, phase, mode = team.state, team.phase, team.mode
            targets = []
            if state in ("running", "paused"):
                if target:
                    resolved = self.resolve_role(team, target)
                    if resolved is None:
                        raise OpError("没有找到这个成员")
                    targets.append(resolved)
                    if state == "paused":
                        team.awaited.clear()
                    if resolved not in team.awaited:
                        team.awaited.append(resolved)
                else:
                    if state == "paused" and mode == "lead" and team.awaited and \
                            all(role != "leader" and team.failed.get(self.tool_of(team, role)) for role in team.awaited):
                        team.awaited.clear()
                        team.awaited.append("leader")
                    targets.extend(team.awaited if team.awaited else
                                   ["writer" if mode == "review" else "leader" if mode == "lead" else team.tools[0]])
                    for role in targets:
                        if role not in team.awaited:
                            team.awaited.append(role)
                if state == "paused":
                    team.state = "running"
                    team.note = ""
                self.save()
            elif mode == "compare" and state in ("choose", "stopped"):
                if target:
                    if target not in team.tools:
                        raise OpError("没有找到这个候选 AI")
                    targets.append(target)
                else:
                    targets.extend(team.tools)
                team.round += 1
                if team.round > team.rounds:
                    team.rounds = min(MAX_TOTAL_ROUNDS, team.round)
                team.state = "running"
                team.note = ""
                team.awaited.clear()
                team.awaited.extend(targets)
                self.save()
            else:
                team.round += 1
                if team.round > team.rounds:
                    team.rounds = min(MAX_TOTAL_ROUNDS, team.round + 2)
                team.state = "running"
                team.note = ""
                team.phase = ""
                team.reminded = False
                team.awaited.clear()
                team.answers.clear()
                self.save()

        self.log(team, "user", ("@" + self.label(self.tool_of(team, self.resolve_role(team, target) or target)) + " "
                                if target else "") + text)

        if state in ("running", "paused"):
            prefix = "【用户插话】\n" if state == "running" else "【用户补充】\n"
            for role in targets:
                terminal = team.members.get(role)
                self.deliver_or_reopen(team, role, terminal, prefix + text)
        elif mode == "compare":
            prompt = "【用户补充要求 · 第 " + str(team.round) + " 轮】\n" + text + \
                "\n\n请在你当前独立工作区中继续完善方案，完成后简要说明改了什么。"
            for tool in targets:
                self.ask(team, tool, tool, prompt, False, team.worktrees.get(tool))
        elif mode == "review":
            self.ask(team, "writer", team.tools[0],
                     "【用户新一轮要求 · 第 " + str(team.round) + " 轮】\n" + text +
                     "\n\n请按新要求继续修改，完成后简明说明改了什么。之后 " + self.label(team.tools[1]) +
                     " 会再次只读审查。", False)
        else:
            rooms = self.rooms(team)
            self.ask(team, "leader", team.tools[0],
                     "【用户新一轮要求 · 第 " + str(team.round) + " 轮】\n" + text + self.rooms_block(team, rooms) +
                     "\n\n请检查新要求并继续分派（ASSIGN 成员名: 任务）或确认完成（DONE: 总结）。", False)
        return self.of(id)

    def pick(self, id, tool):
        tool = (tool or "").strip().lower()
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            if team is None:
                raise OpError("没有找到这个协作任务")
            if team.mode != "compare":
                raise OpError("只有并行比稿任务才可以选择方案")
            if team.state == "running":
                raise OpError("还有 AI 正在执行，请等它们做完或先停止")
            if not team.worktrees:
                raise OpError("这个比稿任务的工作区已经清理")
            wt_path = team.worktrees.get(tool, "")
            base_commit = team.base_commits.get(tool, "")
        note = ""
        if not tool:
            if self.hands.clean_worktrees:
                try:
                    self.hands.clean_worktrees(team.dir, id)
                except Exception:
                    pass
            note = "已放弃所有候选方案并清理工作区"
        else:
            if not wt_path:
                raise OpError("没有找到这个候选方案")
            if self.hands.apply_worktree is None:
                raise OpError("当前环境不支持合并工作区改动")
            applied = self.hands.apply_worktree(team.dir, wt_path, base_commit)
            if self.hands.clean_worktrees:
                try:
                    self.hands.clean_worktrees(team.dir, id)
                except Exception:
                    pass
            note = "已合并 " + self.label(tool) + " 的方案（" + applied + "），其余工作区已清理"
        with self.gate:
            team.worktrees.clear()
            team.base_commits.clear()
            team.picked = tool
            team.state = "done"
            team.note = note
            team.awaited.clear()
            self.save()
        self.log(team, "结果", note)
        return self.of(id)

    def swap(self, id, role, new_tool, note=""):
        role = (role or "").strip()
        new_tool = (new_tool or "").strip().lower()
        note = (note or "").strip()
        if len(note) > 1000:
            raise OpError("补充说明不能超过 1000 字")
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            if team is None:
                raise OpError("没有找到这个协作任务")
        if new_tool not in ((self.hands.available() or []) if self.hands.available else []):
            raise OpError("接手 AI 无效")
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            if team is None:
                raise OpError("没有找到这个协作任务")
            if team.state == "failed":
                raise OpError("这个协作任务没有启动成功，请重新新建")
            if team.mode == "compare":
                raise OpError("并行比稿不支持替换角色")
            prev_state = team.state
            resolved = self.resolve_role(team, role) if role else self.default_swap_role(team)
            if resolved is None:
                raise OpError("没有找到这个角色")
            old_tool = self.tool_of(team, resolved)
            old_terminal = team.members.get(resolved)
            pending = team.pending.get(resolved, "")
            team.failed.pop(new_tool, None)
            was_active = resolved in team.awaited or prev_state != "running"
            launch_now = False
            if team.mode == "review":
                slot = 0 if resolved == "writer" else 1
                team.tools[slot] = new_tool
                team.members.pop(resolved, None)
                if team.phase == "discuss":
                    old_discuss = "discuss:" + old_tool
                    new_discuss = "discuss:" + new_tool
                    if old_discuss in team.members:
                        old_terminal = team.members.pop(old_discuss)
                    if old_discuss in team.awaited:
                        team.awaited.remove(old_discuss)
                        if new_discuss not in team.awaited:
                            team.awaited.append(new_discuss)
                        resolved = new_discuss
                        launch_now = True
                elif was_active:
                    team.state = "running"
                    team.note = ""
                    team.awaited.clear()
                    team.awaited.append(resolved)
                    launch_now = True
            elif resolved == "leader":
                self.promote_leader(team, new_tool)
                team.members.pop("leader", None)
                if was_active:
                    team.state = "running"
                    team.note = ""
                    team.awaited.clear()
                    team.awaited.append("leader")
                    launch_now = True
            elif team.phase == "discuss" and resolved.startswith("discuss:"):
                new_discuss = "discuss:" + new_tool
                if new_tool not in team.tools:
                    team.tools.append(new_tool)
                team.members.pop(resolved, None)
                if resolved in team.awaited:
                    team.awaited.remove(resolved)
                if new_discuss not in team.awaited:
                    team.awaited.append(new_discuss)
                if prev_state != "running":
                    team.state = "running"
                    team.note = ""
                resolved = new_discuss
                launch_now = True
            else:
                if new_tool not in team.tools:
                    team.tools.append(new_tool)
                next_role = "leader" if new_tool == team.tools[0] else new_tool
                if resolved in team.awaited:
                    team.awaited.remove(resolved)
                if prev_state == "paused":
                    team.awaited.clear()
                if next_role not in team.awaited:
                    team.awaited.append(next_role)
                team.state = "running"
                team.note = ""
                resolved = next_role
                launch_now = True
            self.save()
        if old_terminal:
            try:
                self.hands.stop(old_terminal)
            except Exception:
                pass
        if team.mode == "review":
            summary = "已将" + ("审查" if resolved == "reviewer" else "写手") + "从 " + self.label(old_tool) + \
                " 切换为 " + self.label(new_tool)
        elif resolved == "leader":
            summary = "已将领队从 " + self.label(old_tool) + " 切换为 " + self.label(new_tool)
        else:
            summary = "已将 " + self.label(old_tool) + " 的任务转交给 " + self.label(new_tool)
        if note:
            summary += "（" + cut(note, 120) + "）"
        self.log(team, "切换", summary)
        if not launch_now:
            return self.of(id)
        read_only = resolved == "reviewer" or resolved.startswith("discuss:")
        prompt = self.handoff_prompt(team, resolved, old_tool, "用户指定切换", pending, note)
        if team.mode == "lead" and resolved != "leader" and not resolved.startswith("discuss:"):
            self.ask(team, resolved, new_tool, prompt, False)
        else:
            self.open(team, resolved, new_tool, prompt, read_only, None)
        return self.of(id)

    def default_swap_role(self, team):
        failed_awaited = next((role for role in team.awaited if team.failed.get(self.tool_of(team, role))), None)
        if failed_awaited:
            return failed_awaited
        if team.awaited:
            return team.awaited[0]
        if team.mode == "review":
            return "writer"
        return team.tools[1] if len(team.tools) > 1 else "leader"

    @staticmethod
    def promote_leader(team, new_leader):
        team.tools = [new_leader] + [one for one in team.tools if one != new_leader]

    def remove(self, id):
        with self.gate:
            team = next((one for one in self.items if one.id == id), None)
            if team is None:
                return False
            if team.state == "running":
                raise OpError("这个协作任务还在进行，请先停止")
            has_worktrees = bool(team.worktrees)
            dir_name = team.dir
            self.items.remove(team)
            self.save()
        if has_worktrees and self.hands.clean_worktrees:
            try:
                self.hands.clean_worktrees(dir_name, id)
            except Exception:
                pass
        self.tell_changed()
        return True

    @staticmethod
    def resolve_role(team, wanted):
        if not wanted:
            return None
        if wanted in team.members:
            return wanted
        if team.phase == "discuss" and "discuss:" + wanted in team.members:
            return "discuss:" + wanted
        if team.mode == "review":
            if wanted == team.tools[0]:
                return "writer"
            if len(team.tools) > 1 and wanted == team.tools[1]:
                return "reviewer"
        if team.mode == "lead" and wanted == team.tools[0]:
            return "leader"
        if wanted in team.tools:
            return wanted
        return None

    @staticmethod
    def tool_of(team, role):
        if role.startswith("discuss:"):
            return role[8:]
        if role in ("writer", "leader"):
            return team.tools[0]
        if role == "reviewer":
            return team.tools[1] if len(team.tools) > 1 else team.tools[0]
        return role

    def deliver_or_reopen(self, team, role, terminal, words):
        if terminal:
            try:
                self.hands.say(terminal, words)
                return
            except Exception:
                pass
        tool = self.tool_of(team, role)
        wt = team.worktrees.get(tool) if team.mode == "compare" else None
        read = role == "reviewer" or role.startswith("discuss:")
        if role.startswith("discuss:"):
            prompt = self.discuss_first(team, tool) + "\n\n" + words
        elif team.mode == "compare":
            prompt = self.compare_first(team, tool) + "\n\n" + words
        elif team.mode == "review" and role == "writer":
            prompt = self.writer_first(team) + ("" if words == "继续完成上面的任务。" else "\n\n" + words)
        elif team.mode == "review" and role == "reviewer":
            prompt = self.reviewer_ask(team, "（重新接续审查）" + ("" if words == "继续完成上面的任务。" else "\n" + words),
                                       self.safe_changes(team))
        elif team.mode == "lead" and role == "leader":
            prompt = self.leader_first(team, self.rooms(team)) + ("" if words == "继续完成上面的任务。" else "\n\n" + words)
        else:
            prompt = "【协作任务】继续完成这项工作：" + team.goal + "\n\n" + words
        self.open(team, role, tool, prompt, read, wt)

    # ---- the members' turns
    def open(self, team, role, tool, words, read, folder_override):
        with self.gate:
            self.opening += 1
            team.pending[role] = cut(words, 6000)
        try:
            begin_in = self.hands.begin_in or self.hands.begin
            terminal = (begin_in(tool, team.dir, words, read, folder_override) if self.hands.begin_in and folder_override
                        else begin_in(tool, team.dir, words, read))
            with self.gate:
                team.members[role] = terminal
                if role not in team.awaited:
                    team.awaited.append(role)
                self.opening -= 1
                self.save()
                self.gate.notify_all()
            self.log(team, role, "开始：" + cut(words, 240))
        except Exception as problem:
            with self.gate:
                team.state = "failed" if not team.members and len(team.log) <= 1 else "paused"
                team.note = self.label(tool) + " 没有开始：" + (str(problem) if isinstance(problem, (OpError, ValueError)) else "电脑端出错")
                if team.state == "failed":
                    if role in team.awaited:
                        team.awaited.remove(role)
                elif role not in team.awaited:
                    team.awaited.append(role)
                self.opening -= 1
                self.save()
                self.gate.notify_all()
            self.tell_changed()

    def ask(self, team, role, tool, words, read, folder_override=None):
        with self.gate:
            terminal = team.members.get(role)
            team.pending[role] = cut(words, 6000)
            if role not in team.awaited:
                team.awaited.append(role)
            self.save()
        if terminal:
            try:
                self.hands.say(terminal, words)
                self.log(team, role, "收到：" + cut(words, 240))
                return
            except Exception:
                pass            # its conversation was closed: begun anew
        self.open(team, role, tool, words, read, folder_override)

    def end(self, team, state, note):
        with self.gate:
            team.state = state
            team.note = note
            team.awaited.clear()
            self.save()
        self.log(team, "结果", note)

    def turned(self, terminal, how, said):
        """A member's turn ended (its chat said so, or the quiet poll below). `how` is one of
        done / stopped / failed; `said` is what the member said in its turn."""
        with self.gate:
            team = next((one for one in self.items if one.state == "running" and terminal in one.members.values()), None)
            if team is None and self.opening > 0:
                self.gate.wait(0.2)
                team = next((one for one in self.items if one.state == "running" and terminal in one.members.values()), None)
            if team is None:
                return
            role = next(key for key, value in team.members.items() if value == terminal)
            if role not in team.awaited:
                return          # (a turn of its own owner's, from the phone)
            team.awaited.remove(role)
            self.save()
        self.log(team, role, cut(said, 4000))
        if team.mode == "compare":
            self.compared(team, role, how, said)
            return
        if how == "done":
            with self.gate:
                if team.failed.pop(self.tool_of(team, role), None) is not None:
                    self.save()
        elif how == "stopped":
            with self.gate:
                team.state = "paused"
                team.note = self.label(self.tool_of(team, role)) + " 这一轮被停止：点“继续”接着做，或点“换 AI 接手”"
                if role not in team.awaited:
                    team.awaited.append(role)
                self.save()
            self.tell_changed()
            return
        else:
            self.handle_failure(team, role, said)
            return
        if team.phase == "discuss":
            self.discussed(team, role, said)
            return
        if team.mode == "review":
            self.reviewed(team, role, said)
        else:
            self.led(team, role, said)

    def handle_failure(self, team, role, said):
        failed_tool = self.tool_of(team, role)
        reason = failure_reason(said)
        with self.gate:
            team.failed[failed_tool] = reason
            pending = team.pending.get(role, "")
            self.save()

        if team.phase == "discuss":
            self.log(team, "切换", self.label(failed_tool) + " 讨论阶段失败（" + reason + "），其余成员继续")
            self.discussed(team, role, "（" + self.label(failed_tool) + " 未完成讨论：" + reason + "）")
            return

        if team.mode == "lead" and role != "leader":
            report = "【执行失败】" + self.label(failed_tool) + " 这一轮失败（" + reason + "）。"
            if said and said.strip() != reason:
                report += "\n报错信息：" + cut(said, 600)
            if pending:
                report += "\n原分派任务：" + cut(pending, 800)
            report += "\n请将该未完成任务改派给其他可用成员，不要再派给 " + self.label(failed_tool) + "。"
            answers = None
            added_backup = None
            pause_no_member = False
            with self.gate:
                team.answers[role] = report
                if any(one != "leader" for one in team.awaited):
                    self.save()
                else:
                    answers = dict(team.answers)
                    team.answers.clear()
                    has_live_member = any(tool not in team.failed and not self.is_exhausted(self.safe_limits(tool))
                                          for tool in team.tools[1:])
                    if not has_live_member:
                        added_backup = self.fallback_tool(team, failed_tool, team.tools[0])
                        if added_backup:
                            if added_backup not in team.tools:
                                team.tools.append(added_backup)
                        else:
                            pause_no_member = True
                            team.state = "paused"
                            team.note = self.label(failed_tool) + " 失败（" + reason + "），且没有其他可用成员：可点“换 AI 接手”或稍后继续"
                            team.awaited.clear()
                            team.awaited.append("leader")
                    self.save()
            self.log(team, "切换", self.label(failed_tool) + " 失败（" + reason + "），已自动加入 " + self.label(added_backup) +
                     " 并回报领队改派" if added_backup else self.label(failed_tool) + " 失败（" + reason + "），已回报领队改派给其他可用成员")
            if answers is None or pause_no_member:
                self.tell_changed()
                return
            if any(not value.startswith("【执行失败】") for value in answers.values()):
                if team.round >= team.rounds:
                    self.end(team, "done", "已到 " + str(team.rounds) + " 轮，成员最后的结果没有交给领队检查")
                    return
                with self.gate:
                    team.round += 1
                    self.save()
            changes = self.safe_changes(team)
            body = self.results(team, answers, changes, self.rooms(team))
            if added_backup:
                body += "\n\n【自动补充成员】已将 " + self.label(added_backup) + "（" + added_backup + "）加入协作队伍，可直接用 ASSIGN " + added_backup + ": 任务内容 改派。"
            self.ask(team, "leader", team.tools[0], body, False)
            return

        if team.mode == "lead":
            backup = self.fallback_tool(team, failed_tool)
            if backup:
                with self.gate:
                    self.promote_leader(team, backup)
                    team.members.pop("leader", None)
                    team.awaited.clear()
                    team.awaited.append("leader")
                    self.save()
                self.log(team, "切换", self.label(failed_tool) + "（领队）失败（" + reason + "），已自动由 " +
                         self.label(backup) + " 接任领队继续")
                self.open(team, "leader", backup, self.handoff_prompt(team, "leader", failed_tool,
                         "因 " + reason + " 中断", pending, ""), False, None)
                return
            with self.gate:
                team.state = "paused"
                team.note = self.label(failed_tool) + "（领队）失败（" + reason + "），没有可用备用 AI：可点“换 AI 接手”或稍后继续"
                if "leader" not in team.awaited:
                    team.awaited.append("leader")
                self.save()
            self.tell_changed()
            return

        slot = 0 if role == "writer" else 1
        other = team.tools[1 - slot] if len(team.tools) > 1 else ""
        replacement = self.fallback_tool(team, failed_tool, other)
        if replacement:
            with self.gate:
                team.tools[slot] = replacement
                team.members.pop(role, None)
                team.awaited.clear()
                team.awaited.append(role)
                self.save()
            self.log(team, "切换", self.label(failed_tool) + "（" + ("写手" if role == "writer" else "审查") +
                     "）失败（" + reason + "），已自动切换为 " + self.label(replacement) + " 继续")
            self.open(team, role, replacement, self.handoff_prompt(team, role, failed_tool, "因 " + reason + " 中断",
                     pending, ""), role == "reviewer", None)
            return
        with self.gate:
            team.state = "paused"
            team.note = self.label(failed_tool) + " 失败（" + reason + "），没有其他可用备用 AI：可点“换 AI 接手”指定接替，或稍后继续"
            if role not in team.awaited:
                team.awaited.append(role)
            self.save()
        self.tell_changed()

    def discussed(self, team, role, said):
        collected = None
        with self.gate:
            team.answers[role] = said
            if any(one.startswith("discuss:") for one in team.awaited):
                self.save()
                return
            collected = dict(team.answers)
            team.answers.clear()
            team.phase = ""
            self.save()
        discussion = "\n\n".join("### " + self.label(self.tool_of(team, key)) + "\n" + cut(value, 5000)
                                 for key, value in collected.items())
        if team.mode == "review":
            writer_tool = team.tools[0]
            if team.failed.get(writer_tool):
                backup = self.fallback_tool(team, writer_tool, team.tools[1])
                if backup:
                    team.tools[0] = backup
                    self.log(team, "切换", self.label(writer_tool) + " 讨论阶段不可用，写手已自动切换为 " + self.label(backup))
                    writer_tool = backup
            self.open(team, "writer", writer_tool, self.writer_first(team, discussion), False, None)
        else:
            leader_tool = team.tools[0]
            if team.failed.get(leader_tool):
                backup = self.fallback_tool(team, leader_tool)
                if backup:
                    self.promote_leader(team, backup)
                    self.log(team, "切换", self.label(leader_tool) + " 不可用，领队已自动切换为 " + self.label(backup))
                    leader_tool = backup
            self.open(team, "leader", leader_tool, self.leader_first(team, self.rooms(team), discussion), False, None)

    def compared(self, team, tool, how, said):
        wt = team.worktrees.get(tool)
        base_commit = team.base_commits.get(tool)
        diff = stat = ""
        if wt:
            if self.hands.worktree_diff:
                try:
                    diff = self.hands.worktree_diff(wt, base_commit) or ""
                except Exception:
                    pass
            if self.hands.worktree_stat:
                try:
                    stat = self.hands.worktree_stat(wt, base_commit) or ""
                except Exception:
                    pass
        with self.gate:
            team.summaries[tool] = cut(said, 6000) if how == "done" else \
                (("（这一轮已停止）" if how == "stopped" else "（执行出错）") if not (said or "").strip() else cut(said, 6000))
            team.diffs[tool] = cut(diff, 30000)
            team.stats[tool] = stat or ("没有文件改动" if how == "done" else "未完成")
            all_done = not team.awaited
            any_ok = any(not value.startswith("（执行出错") and not value.startswith("（这一轮已停止")
                         for value in team.summaries.values())
            if all_done:
                team.state = "choose" if any_ok else "paused"
                team.note = "各候选方案已完成，对比改动后选一个合并到主目录" if any_ok else "候选 AI 均未完成，请检查后重试或删除"
            self.save()
        if all_done:
            self.log(team, "结果", "候选方案已就绪，等待对比选择" if any_ok else "候选方案未完成")
        else:
            self.tell_changed()

    def reviewed(self, team, role, said):
        if role == "writer":
            self.ask(team, "reviewer", team.tools[1] if len(team.tools) > 1 else team.tools[0],
                     self.reviewer_ask(team, said, self.safe_changes(team)), True)
            return
        passed = verdict(said)
        if passed is True:
            self.end(team, "done", "审查通过（第 " + str(team.round) + " 轮）")
            return
        if team.round >= team.rounds:
            self.end(team, "done", "已到 " + str(team.rounds) + " 轮，最后一轮的审查意见没有处理")
            return
        with self.gate:
            team.round += 1
            self.save()
        self.ask(team, "writer", team.tools[0], self.writer_again(team, said), False)

    def led(self, team, role, said):
        if role == "leader":
            summary = self.finished(said)
            tasks = self.assigned(said, team.tools[1:])
            if not tasks and summary is not None:
                self.end(team, "done", "领队确认完成：" + cut(summary, 600))
                return
            if not tasks:
                with self.gate:
                    reminded = team.reminded
                    team.reminded = True
                    self.save()
                if reminded:
                    with self.gate:
                        team.state = "paused"
                        team.note = "领队没有分派任务，也没有结束：点“继续”再问它一次"
                        team.awaited.clear()
                        team.awaited.append("leader")
                        self.save()
                    self.tell_changed()
                    return
                self.ask(team, "leader", team.tools[0], REMIND, False)
                return
            with self.gate:
                team.reminded = False
                team.answers.clear()
                team.awaited.clear()
                for member in tasks:
                    if member not in team.awaited:
                        team.awaited.append(member)
                self.save()
            for member, task in tasks.items():
                self.ask(team, member, member, self.member_task(team, task), False)
            return
        answers = None
        with self.gate:
            team.answers[role] = said
            if any(one != "leader" for one in team.awaited):
                self.save()
                return
            answers = dict(team.answers)
            team.answers.clear()
            self.save()
        if team.round >= team.rounds:
            self.end(team, "done", "已到 " + str(team.rounds) + " 轮，成员最后的结果没有交给领队检查")
            return
        with self.gate:
            team.round += 1
            self.save()
        self.ask(team, "leader", team.tools[0],
                 self.results(team, answers, self.safe_changes(team), self.rooms(team)), False)

    # ---- what the members are told
    def safe_limits(self, tool):
        if self.hands.limits is None:
            return None
        try:
            return self.hands.limits(tool)
        except Exception:
            return None

    @staticmethod
    def is_exhausted(windows):
        return bool(windows) and any(round(one["used"]) >= 100 for one in windows)

    def rooms(self, team):
        rooms = {}
        with self.gate:
            failed = dict(team.failed)
            tools = list(dict.fromkeys(team.tools))
        if self.hands.limits is None and not failed:
            return rooms
        for tool in tools:
            is_failed = tool in failed
            if self.hands.limits is not None:
                room = self.room(self.safe_limits(tool))
                rooms[tool] = room + ("；当前不可用（" + failed[tool] + "，请勿再派工）" if is_failed else "")
            elif is_failed:
                rooms[tool] = "不可用（" + failed[tool] + "，请勿再派工）"
        return rooms

    @staticmethod
    def room(windows):
        if not windows:
            return "额度未知"
        said = []
        for one in windows:
            name = {"five_hour": "5 小时窗口", "seven_day": "本周"}.get(one["name"], one["name"])
            said.append(name + "已用 " + str(round(one["used"])) + "%" +
                        ("（" + _when(one["resets"]) + " 重置）" if one.get("resets", 0) > 0 else ""))
        tag = "，已用尽（不可派工）" if Teams.is_exhausted(windows) else \
            ("，紧张" if any(round(one["used"]) >= 80 for one in windows) else "")
        return "额度：" + "，".join(said) + tag

    def fallback_tool(self, team, failed_tool, avoid_tool=""):
        with self.gate:
            failed = dict(team.failed)
            candidates = [one for one in team.tools if one and one != failed_tool and one != avoid_tool]
        if self.hands.available:
            try:
                for tool in self.hands.available() or []:
                    if tool and tool != failed_tool and tool != avoid_tool and tool not in candidates:
                        candidates.append(tool)
            except Exception:
                pass
        usable = []
        for index, tool in enumerate(candidates):
            if tool in failed:
                continue
            windows = self.safe_limits(tool)
            if self.is_exhausted(windows):
                continue
            score = max((one["used"] for one in windows), default=50.0) if windows else 50.0
            usable.append((score, index, tool))
        return min(usable)[2] if usable else None

    def safe_changes(self, team):
        if self.hands.changes is None:
            return ""
        try:
            return self.hands.changes(team.dir) or ""
        except Exception:
            return ""

    @staticmethod
    def extra_rules(team):
        return "" if not team.rules else "\n\n【分工偏好】\n" + cut(team.rules, 1000)

    def rooms_block(self, team, rooms):
        if not rooms:
            return ""
        lines = ["- " + self.label(tool) + ("（你）" if tool == team.tools[0] else "") + "：" + rooms[tool]
                 for tool in dict.fromkeys(team.tools) if tool in rooms]
        return "\n\n## 当前额度\n" + "\n".join(lines) if lines else ""

    def discuss_first(self, team, tool):
        return "【协作任务 · 方案讨论（只读）】目标：" + team.goal + self.extra_rules(team) + \
            "\n\n正式动手修改代码前，各位 AI 先只读查看相关代码并简明提出自己的方案建议（3–6 条要点：核心思路、涉及文件、潜在风险与边界条件）。不要修改任何文件。"

    def writer_first(self, team, discussion=""):
        return "【协作任务 · 写】" + team.goal + self.extra_rules(team) + \
            ("" if not discussion else "\n\n## 前期方案讨论汇总\n" + cut(discussion, 12000)) + \
            "\n\n请直接干脆地完成任务，完成后简明说明做了什么、改了哪些文件。之后 " + self.label(team.tools[1] if len(team.tools) > 1 else team.tools[0]) + \
            " 会只读审查你的改动，有问题再交回修改。"

    def reviewer_ask(self, team, words, changes):
        return "【协作任务 · 审查 · 第 " + str(team.round) + " 轮】另一个 AI（" + self.label(team.tools[0]) + "）正在完成这项任务：\n" + \
            team.goal + self.extra_rules(team) + "\n\n它这一轮的说明：\n" + cut(words, 6000) + \
            ("\n\n改动：\n" + cut(changes, 30000) if changes else "") + \
            "\n\n请只审查，不要修改任何文件。没有必须修改的实质问题就直接通过，不要吹毛求疵。回答的第一行只写 REVIEW: PASS 或 REVIEW: CHANGES，之后逐条列出必须修改的问题与建议。"

    def writer_again(self, team, review):
        return "【协作任务 · 审查意见 · 第 " + str(team.round) + " 轮】" + \
            self.label(team.tools[1] if len(team.tools) > 1 else team.tools[0]) + " 的审查意见：\n" + cut(review, 12000) + \
            "\n\n请据此修改，完成后简要说明改了什么。"

    def leader_first(self, team, rooms, discussion=""):
        def room(tool):
            return "；" + rooms[tool] if tool in rooms else ""
        members = "\n".join("- " + tool + "：" + self.label(tool) + ("，" + STRENGTHS[tool] if tool in STRENGTHS else "") + room(tool)
                            for tool in team.tools[1:])
        return "【协作任务 · 领队】目标：" + team.goal + self.extra_rules(team) + \
            ("" if not discussion else "\n\n## 成员前期方案讨论（只读）\n" + cut(discussion, 12000)) + \
            "\n\n你负责决策、按任务特点高效干净地分工、验收结果并在达标时结束。成员都在同一个项目文件夹中工作：\n" + members + \
            ("\n你自己（" + self.label(team.tools[0]) + "）的" + rooms[team.tools[0]] if team.tools[0] in rooms else "") + \
            "\n\n分工原则：\n1. 按具体任务按需派工，不必每次把所有成员都派满；不需要的成员可以不派。\n" \
            "2. 并行分派时写清各自负责的文件和验收标准，避免多人改同一批文件。\n" \
            "3. 需要核验时采用交叉检查（谁写不由谁审），优先让 Claude Code 或 Antigravity CLI 交叉检查，不必把纯检查浪费在 Codex 上。\n" \
            "4. 结果达标后立刻结束，不要为了凑轮次空转。" + \
            ("\n5. 按额度分配工作量：额度已用尽或不可用的成员不要再派工；额度紧张（任一窗口已用 80% 以上）的成员少派或不派，"
             "只给小而必要的任务，工作多交给额度充足的成员；你自己的额度紧张时，回答保持简短，把实现和检查交给成员。额度未知的成员按正常对待。"
             if rooms else "") + \
            "\n\n分派时，在回答的最后每行写一条（成员名用上面的英文名）：\nASSIGN 成员名: 任务内容\n全部完成时，回答的最后单独一行写：\nDONE: 总结\n分派出去的工作不要自己动手做。"

    def member_task(self, team, task):
        return "【协作任务 · 成员】领队（" + self.label(team.tools[0]) + "）分派给你的任务：\n" + task + \
            "\n\n整体目标：" + team.goal + "\n\n完成后简要说明你做了什么、改了哪些文件。"

    def compare_first(self, team, tool):
        return "【协作任务 · 并行比稿】目标：" + team.goal + self.extra_rules(team) + \
            "\n\n你正在该项目的独立 Git worktree 副本中工作，不会与其他候选 AI 冲突。请直接完成你的最佳实现并验证关键路径，" \
            "完成后简明说明你的方案思路、改了哪些文件以及取舍理由。用户会对比各候选方案并选择一个合并。"

    def results(self, team, answers, changes, rooms):
        return "【协作任务 · 成员结果 · 第 " + str(team.round) + " 轮】\n\n" + \
            "\n\n".join("## " + self.label(key) + "\n" + cut(value, 8000) for key, value in answers.items()) + \
            ("\n\n## 文件夹中的改动\n" + cut(changes, 20000) if changes else "") + \
            self.rooms_block(team, rooms) + \
            "\n\n请检查结果，再分派下一步（ASSIGN 成员名: 任务）或结束（DONE: 总结）。"

    def handoff_prompt(self, team, role, from_tool, why, pending, user_note):
        changes = self.safe_changes(team)
        extra = ("" if not why else "\n\n【接手说明】原负责成员（" + self.label(from_tool) + "）" + why + "，现在由你接手继续完成。") + \
            ("" if not user_note else "\n【用户补充】" + user_note)
        if role.startswith("discuss:"):
            return self.discuss_first(team, self.tool_of(team, role)) + extra
        if team.mode == "review" and role == "writer":
            return self.writer_first(team) + \
                ("" if not pending or pending == self.writer_first(team) else "\n\n## 当前待完成的任务/意见\n" + cut(pending, 6000)) + \
                ("\n\n## 当前工作区已有改动\n" + cut(changes, 20000) if changes else "") + extra + \
                "\n\n请基于当前工作区已有改动继续完成，不要推倒重做已经正确的部分。"
        if team.mode == "review" and role == "reviewer":
            return self.reviewer_ask(team, "原审查 AI（" + self.label(from_tool) + "）未能完成本轮审查，请你接手只读审查。" +
                                     ("" if not user_note else "\n用户补充：" + user_note), changes)
        if team.mode == "lead" and role == "leader":
            recent = "\n\n".join("[" + step["who"] + "] " + cut(step["text"], 800) for step in team.log[-10:])
            return self.leader_first(team, self.rooms(team)) + \
                ("\n\n## 此前协作进展\n" + cut(recent, 8000) if recent else "") + \
                ("\n\n## 当前工作区改动\n" + cut(changes, 16000) if changes else "") + extra
        return "【协作任务 · 接手成员任务】\n整体目标：" + team.goal + self.extra_rules(team) + \
            ("\n\n原分派给 " + self.label(from_tool) + " 的任务：\n" + cut(pending, 4000) if pending else "") + \
            ("\n\n## 当前工作区已有改动\n" + cut(changes, 16000) if changes else "") + extra + \
            "\n\n请基于当前进展继续完成该任务，完成后简要说明你做了什么、改了哪些文件。"

    @staticmethod
    def finished(said):
        found = DONE.search(said or "")
        return found.group(1).strip() if found else None

    @staticmethod
    def assigned(said, members):
        tasks = {}
        known = set(members)
        for match in ASSIGN.finditer(said or ""):
            tool = match.group(1).lower()
            task = match.group(2).strip()
            if tool not in known or not task:
                continue
            tasks[tool] = tasks.get(tool, "") + ("\n" if tool in tasks else "") + task
        return tasks


def _when(seconds):
    moment = time.localtime(max(0, seconds))
    return time.strftime("%H:%M", moment) if time.strftime("%Y-%m-%d", moment) == time.strftime("%Y-%m-%d") \
        else time.strftime("%m月%d日 %H:%M", moment)
