import * as vscode from 'vscode';
import * as fs from 'fs';
import * as fsp from 'fs/promises';
import * as os from 'os';
import * as path from 'path';
import * as crypto from 'crypto';

const EXTERNAL_TYPES = new Set(['llm', 'chat', 'ask']);
const SWEEP_MS = 5000;

let watcher: fs.FSWatcher | undefined;
let sweepTimer: NodeJS.Timeout | undefined;
let statusItem: vscode.StatusBarItem;
let output: vscode.OutputChannel;
const inFlight = new Set<string>();
let scanning = false;

function busRoot(): string {
  const configured = vscode.workspace.getConfiguration('copilotBus').get<string>('root');
  if (configured && configured.trim()) {
    return configured.trim();
  }
  return path.join(os.homedir(), 'OneDrive - Microsoft', 'CopilotBus');
}

const inboxDir = () => path.join(busRoot(), 'inbox');
const outboxDir = () => path.join(busRoot(), 'outbox');
const processedDir = () => path.join(busRoot(), 'processed');

async function ensureDirs(): Promise<void> {
  for (const d of [inboxDir(), outboxDir(), processedDir()]) {
    await fsp.mkdir(d, { recursive: true });
  }
}

async function writeAtomic(dest: string, text: string): Promise<void> {
  const tmp = path.join(path.dirname(dest), `.${crypto.randomUUID()}.tmp`);
  await fsp.writeFile(tmp, text, 'utf8');
  await fsp.rename(tmp, dest);
}

async function moveToProcessed(file: string): Promise<void> {
  let dest = path.join(processedDir(), path.basename(file));
  if (fs.existsSync(dest)) {
    dest = path.join(processedDir(), `${path.basename(file, '.json')}-${crypto.randomUUID()}.json`);
  }
  await fsp.rename(file, dest);
}

async function pickModel(): Promise<vscode.LanguageModelChat | undefined> {
  const family = vscode.workspace.getConfiguration('copilotBus').get<string>('modelFamily')?.trim();
  const selector: vscode.LanguageModelChatSelector = family
    ? { vendor: 'copilot', family }
    : { vendor: 'copilot' };
  const models = await vscode.lm.selectChatModels(selector);
  return models[0];
}

async function answer(prompt: string, system: string | undefined, token: vscode.CancellationToken): Promise<string> {
  const model = await pickModel();
  if (!model) {
    throw new Error('No Copilot chat model available (is GitHub Copilot signed in?).');
  }
  const content = system ? `${system}\n\n${prompt}` : prompt;
  const messages = [vscode.LanguageModelChatMessage.User(content)];
  const response = await model.sendRequest(messages, {}, token);
  let text = '';
  for await (const fragment of response.text) {
    text += fragment;
  }
  return text.trim();
}

async function processFile(file: string): Promise<void> {
  if (inFlight.has(file)) {
    return;
  }
  inFlight.add(file);
  try {
    let raw: string;
    try {
      raw = await fsp.readFile(file, 'utf8');
    } catch {
      return; // still syncing / already moved
    }

    let msg: any;
    try {
      msg = JSON.parse(raw);
    } catch {
      return; // partial write; a later sweep retries
    }

    const type = String(msg.type ?? '').toLowerCase();
    if (!EXTERNAL_TYPES.has(type)) {
      return; // the .NET worker owns this type
    }

    const id: string = msg.id ?? path.basename(file, '.json');
    output.appendLine(`[${new Date().toISOString()}] answering ${id} (${type})`);

    const cts = new vscode.CancellationTokenSource();
    try {
      const text = await answer(String(msg.prompt ?? ''), msg.system, cts.token);
      msg.status = 'done';
      msg.result = text;
      msg.error = null;
    } catch (err: any) {
      msg.status = 'failed';
      msg.error = err?.message ?? String(err);
      output.appendLine(`  failed: ${msg.error}`);
    } finally {
      cts.dispose();
    }
    msg.completedAt = new Date().toISOString();
    msg.origin = 'extension:copilot';

    await writeAtomic(path.join(outboxDir(), `${id}.json`), JSON.stringify(msg, null, 2));
    await moveToProcessed(file);
    output.appendLine(`  -> outbox/${id}.json`);
  } finally {
    inFlight.delete(file);
  }
}

async function scanInbox(): Promise<void> {
  if (scanning) {
    return;
  }
  scanning = true;
  try {
    await ensureDirs();
    const entries = await fsp.readdir(inboxDir()).catch(() => [] as string[]);
    for (const name of entries) {
      if (name.toLowerCase().endsWith('.json')) {
        await processFile(path.join(inboxDir(), name));
      }
    }
  } finally {
    scanning = false;
  }
}

async function start(): Promise<void> {
  await stop();
  await ensureDirs();
  watcher = fs.watch(inboxDir(), () => { void scanInbox(); });
  sweepTimer = setInterval(() => { void scanInbox(); }, SWEEP_MS);
  void scanInbox();
  statusItem.text = '$(radio-tower) CopilotBus';
  statusItem.tooltip = `Answering llm requests in ${inboxDir()}`;
  statusItem.show();
  output.appendLine(`Started. Watching ${inboxDir()}`);
}

async function stop(): Promise<void> {
  watcher?.close();
  watcher = undefined;
  if (sweepTimer) {
    clearInterval(sweepTimer);
    sweepTimer = undefined;
  }
  statusItem.text = '$(circle-slash) CopilotBus off';
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  output = vscode.window.createOutputChannel('CopilotBus');
  statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  context.subscriptions.push(output, statusItem);
  context.subscriptions.push(
    vscode.commands.registerCommand('copilotBus.start', () => start()),
    vscode.commands.registerCommand('copilotBus.stop', () => stop())
  );
  await start();
}

export function deactivate(): void {
  void stop();
}
