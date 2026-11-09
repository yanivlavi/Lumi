import glob
import json
import pathlib
import re
import subprocess
import time
import urllib.request
import xml.etree.ElementTree as ET
import websocket

OUT = pathlib.Path("AndroidHtmlResults")
OUT.mkdir(exist_ok=True)
PACKAGE = "com.lumi.mobile.preview"
report = {"complete": False, "checks": []}
log_stream = None
log_file = None


def adb(*args, binary=False):
    return subprocess.check_output(["adb", *args], text=not binary, timeout=45)

def process_id():
    result = subprocess.run(["adb", "shell", "pidof", PACKAGE], capture_output=True,
                            text=True, timeout=10)
    if result.returncode not in (0, 1):
        result.check_returncode()
    return result.stdout.strip()


def wait(test, description, timeout=45):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = test()
        if value:
            return value
        time.sleep(0.25)
    raise AssertionError("Timed out: " + description)


def targets():
    try:
        with urllib.request.urlopen("http://localhost:9222/json", timeout=2) as response:
            return json.load(response)
    except (OSError, ValueError):
        return []


def snapshot(name):
    adb("shell", "uiautomator", "dump", "/sdcard/window.xml")
    xml = adb("shell", "cat", "/sdcard/window.xml")
    (OUT / (name + ".xml")).write_text(xml)
    return ET.fromstring(xml)


def tap_node(label, snapshot_name):
    tree = snapshot(snapshot_name)
    nodes = [node for node in tree.iter("node")
             if label.lower() in (node.get("text", "") + " " + node.get("content-desc", "")).lower()
             and node.get("enabled") == "true"]
    for node in nodes:
        bounds = [int(n) for n in re.findall(r"\d+", node.get("bounds", ""))]
        if len(bounds) == 4 and bounds[2] > bounds[0] and bounds[3] > bounds[1]:
            adb("shell", "input", "tap", str((bounds[0] + bounds[2]) // 2),
                str((bounds[1] + bounds[3]) // 2))
            return True
    return False


class Cdp:
    def __init__(self, target):
        self.id = 0
        self.contexts = []
        self.ws = websocket.create_connection(target["webSocketDebuggerUrl"], timeout=8,
                                              suppress_origin=True)
        self.call("Runtime.enable")

    def call(self, method, params=None):
        self.id += 1
        self.ws.send(json.dumps({"id": self.id, "method": method, "params": params or {}}))
        while True:
            message = json.loads(self.ws.recv())
            if message.get("method") == "Runtime.executionContextCreated":
                self.contexts.append(message["params"]["context"]["id"])
            elif message.get("method") == "Runtime.executionContextsCleared":
                self.contexts.clear()
            elif message.get("method") == "Runtime.executionContextDestroyed":
                context = message["params"]["executionContextId"]
                self.contexts = [item for item in self.contexts if item != context]
            if message.get("id") == self.id:
                if "error" in message:
                    if message["error"].get("message") == "Cannot find context with specified id":
                        return {}
                    raise AssertionError(message["error"])
                return message.get("result", {})

    def evaluate(self, expression, context):
        result = self.call("Runtime.evaluate", {
            "expression": expression, "contextId": context, "returnByValue": True})
        if "exceptionDetails" in result:
            return None
        return result.get("result", {}).get("value")

    def verify_html(self):
        self.call("Runtime.evaluate", {"expression": "0"})
        for context in list(self.contexts):
            content = self.evaluate("document.body ? document.body.innerText : ''", context)
            if content and "End of the complete HTML file." in content:
                assert len(content) > 120000, len(content)
                self.evaluate("document.querySelector('button').click()", context)
                assert self.evaluate("document.querySelector('#count').textContent", context) == "1"
                self.evaluate("window.__ticks=0;setInterval(()=>window.__ticks++,25)", context)
                return True
        return False


def verify_open(label):
    target = wait(lambda: next(iter(targets()), None), "HTML WebView target")
    cdp = Cdp(target)
    try:
        wait(cdp.verify_html, "complete interactive HTML document")
    finally:
        cdp.ws.close()
    report["checks"].append(label + ": complete 120k payload and JavaScript")
    return target["id"]


def verify_closed(pid, old_target, label):
    wait(lambda: all(item["id"] != old_target for item in targets()), "WebView destroyed")
    assert process_id() == pid, "Preview app crashed or restarted"
    report["checks"].append(label + ": same process alive, old WebView target destroyed")


try:
    apk = glob.glob(".candidate-apk/src/Lumi.Mobile.Android/bin/Debug/net10.0-android/**/*-Signed.apk",
                    recursive=True)
    assert len(apk) == 1, apk
    adb("shell", "wm", "size", "824x1784")
    adb("shell", "wm", "density", "320")
    adb("install", "-r", apk[0])
    component = adb("shell", "cmd", "package", "resolve-activity", "--brief", PACKAGE).strip().splitlines()[-1]
    adb("logcat", "-c")
    log_file = (OUT / "logcat.txt").open("w")
    log_stream = subprocess.Popen(["adb", "logcat"], stdout=log_file, stderr=subprocess.STDOUT)
    adb("shell", "am", "start", "-n", component, "--ez", "lumi.design_preview", "true",
        "--es", "lumi.open_file", "interactive-report.html")
    pid = wait(process_id, "Preview process", timeout=120)
    adb("forward", "tcp:9222", "localabstract:webview_devtools_remote_" + pid)
    old_target = verify_open("initial")
    snapshot("01-html-open")
    adb("shell", "input", "keyevent", "4")
    verify_closed(pid, old_target, "Android Back")
    snapshot("02-after-back")
    for iteration in range(3):
        found = tap_node("interactive-report.html", f"reopen-{iteration}")
        if not found:
            for attempt in range(4):
                adb("shell", "input", "swipe", "500", "700", "500", "1700", "350")
                if tap_node("interactive-report.html", f"find-file-{iteration}-{attempt}"):
                    found = True
                    break
        assert found, "HTML file chip is not exposed by Android accessibility"
        old_target = verify_open(f"reopen {iteration + 1}")
        assert tap_node("Back", f"close-{iteration}"), "Preview Back button not found"
        verify_closed(pid, old_target, f"UI Back {iteration + 1}")
    adb("shell", "wm", "size", "1600x1400")
    adb("shell", "wm", "density", "240")
    assert wait(lambda: tap_node("interactive-report.html", "tablet-file"), "tablet HTML chip")
    old_target = verify_open("two-pane preview")
    assert tap_node("fold-layout-metrics.csv", "switch-to-csv"), "CSV file chip not found"
    verify_closed(pid, old_target, "switch HTML to CSV")
    assert tap_node("interactive-report.html", "switch-to-html"), "HTML chip not found"
    old_target = verify_open("switch CSV to HTML")
    assert tap_node("Close preview", "tablet-close"), "Close preview button not found"
    verify_closed(pid, old_target, "two-pane Close preview")
    report["complete"] = True
except Exception as error:
    report["error"] = repr(error)
    try:
        (OUT / "failure.png").write_bytes(adb("exec-out", "screencap", "-p", binary=True))
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as capture_error:
        report["capture_error"] = repr(capture_error)
    raise
finally:
    if log_stream is not None:
        log_stream.terminate()
        log_stream.wait(timeout=15)
        log_file.close()
    (OUT / "report.json").write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))
