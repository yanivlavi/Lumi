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


def adb(*args, binary=False):
    return subprocess.check_output(["adb", *args], text=not binary, timeout=45)


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
    (OUT / (name + ".png")).write_bytes(adb("exec-out", "screencap", "-p", binary=True))
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
            if message.get("id") == self.id:
                if "error" in message:
                    raise AssertionError(message["error"])
                return message.get("result", {})

    def evaluate(self, expression, context):
        result = self.call("Runtime.evaluate", {
            "expression": expression, "contextId": context, "returnByValue": True})
        if "exceptionDetails" in result:
            return None
        return result.get("result", {}).get("value")

    def verify_html(self):
        for context in self.contexts:
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
    assert adb("shell", "pidof", PACKAGE).strip() == pid, "Preview app crashed or restarted"
    report["checks"].append(label + ": same process alive, old WebView target destroyed")


try:
    apk = glob.glob("src/Lumi.Mobile.Android/bin/Debug/net10.0-android/**/*-Signed.apk",
                    recursive=True)
    assert len(apk) == 1, apk
    adb("install", "-r", apk[0])
    component = adb("shell", "cmd", "package", "resolve-activity", "--brief", PACKAGE).strip().splitlines()[-1]
    adb("logcat", "-c")
    adb("shell", "am", "start", "-n", component, "--ez", "lumi.design_preview", "true",
        "--es", "lumi.open_file", "interactive-report.html")
    pid = wait(lambda: adb("shell", "pidof", PACKAGE).strip(), "Preview process")
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
    report["complete"] = True
except Exception as error:
    report["error"] = repr(error)
    snapshot("failure")
    raise
finally:
    (OUT / "report.json").write_text(json.dumps(report, indent=2))
    (OUT / "logcat.txt").write_text(adb("logcat", "-d"), errors="replace")
    print(json.dumps(report, indent=2))
