using System.Text;

namespace FengSync.Lan;

/// <summary>
/// The page a phone opens when it scans the pairing code, served at <c>GET /p</c>.
/// </summary>
/// <remarks>
/// <para>
/// A string constant in here rather than a file on disk, for two reasons this repository has already learned:
/// a loose file rides in the hash of every release manifest, and an embedded resource is a mechanism nothing
/// else in this codebase uses. Everything the page needs is already in <see cref="LanApi"/> and
/// <see cref="LanProtocol"/>, and it contacts no other origin — no framework, no build step, no request that
/// leaves the local network.
/// </para>
/// <para>
/// It uses <c>crypto.getRandomValues</c> and deliberately never <c>crypto.subtle</c>: the second exists only in
/// a secure context, and this link is plain HTTP to a private address, so a signature the page cannot compute
/// is not a design option. That is the same reason the protocol carries a bearer token and no request
/// signature.
/// </para>
/// <para>
/// The token goes into <c>sessionStorage</c> and nowhere else — never into the address, which a photograph of a
/// QR code already is, and never into <c>localStorage</c>, which would outlive the tab that was handed a
/// one-time code. A browser cannot attach a header to a download link, so the long-lived credential also
/// arrives as an HttpOnly cookie set at pairing; the page never reads that cookie, and no URL here ever carries
/// a token.
/// </para>
/// </remarks>
public static class LanPhonePage
{
    /// <summary>The media type the route answers with; the charset is added by the framework from <see cref="Body"/>.</summary>
    public const string ContentType = "text/html";

    /// <summary>The page is written in Chinese and English and switches itself, so its bytes are plain UTF-8 with no preamble.</summary>
    public static readonly Encoding Body = new UTF8Encoding(false);

    /// <summary>
    /// The budget the page is held to. It is a convenience surface served to somebody else's device over a local
    /// network, so it carries no framework, no build step, and no request to anywhere but this machine — and with
    /// the wording cut back to what a phone screen can show, that is two language tables and one readable
    /// sentence for every refusal the wire answers with. The number guards against the page quietly becoming an
    /// application; it is measured, not invented.
    /// </summary>
    public const int MaximumBytes = 13 * 1024;

    /// <summary>
    /// The document, with its line endings settled. A checkout that stores CRLF would otherwise ship a page a
    /// few hundred bytes larger than the one this budget was measured on, and the served bytes would depend on
    /// whose machine built them.
    /// </summary>
    public static readonly string Html = Document.Replace("\r\n", "\n");

    private const string Document = """
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>Feng Sync</title>
<style>
body{margin:0;padding:14px;background:#11141b;color:#e9ecf3;font:16px/1.5 system-ui,sans-serif}
h1{font-size:17px;margin:0 0 2px}
h2{font-size:14px;margin:0 0 10px;color:#9aa3b8;font-weight:600}
p{margin:8px 0}
small{color:#9aa3b8;font-size:13px}
section{border:1px solid #262b3a;border-radius:12px;padding:12px;margin:0 0 12px}
button{font:inherit;background:#3b82f6;color:#fff;border:0;border-radius:8px;padding:10px 14px}
.g{background:#1b2030;color:#e9ecf3;border:1px solid #2c3346}
input[type=text]{font:inherit;width:100%;box-sizing:border-box;background:#0c0f15;color:#e9ecf3;border:1px solid #2c3346;border-radius:8px;padding:12px;text-transform:uppercase}
ul{list-style:none;margin:0;padding:0}
li{border-top:1px solid #262b3a;padding:9px 0}
li b{display:block;word-break:break-all}
a{color:#7fb0ff;text-decoration:none}
.b{height:4px;background:#262b3a;border-radius:2px;margin-top:6px;overflow:hidden}
.b i{display:block;height:100%;width:0;background:#3b82f6}
.hd{display:none}
</style>
</head>
<body>
<h1 id="h1">Feng Sync</h1>
<p><small id="who"></small></p>
<section id="pair">
<h2 id="ph"></h2>
<p><small id="hint"></small></p>
<p><input id="code" type="text" autocomplete="off" maxlength="12"></p>
<p><button id="go"></button></p>
<p><small id="err"></small></p>
</section>
<section id="send" class="hd">
<h2 id="sh"></h2>
<p><input id="f" type="file" multiple></p>
<p id="dp"><input id="d" type="file" webkitdirectory multiple></p>
<p><small id="snote"></small></p>
<ul id="upl"></ul>
</section>
<section id="get" class="hd">
<h2 id="gh"></h2>
<p><button id="rl" class="g"></button> <small id="cnt"></small></p>
<ul id="dwl"></ul>
<p><small id="zip"></small></p>
</section>
<script>
(function () {
"use strict";
var zh = (navigator.language || "").toLowerCase().indexOf("zh") === 0;
var L = zh ? {
title: "局域网快传", pair: "配对", hint: "电脑窗口上的六位码。扫码已经替你填好了。",
go: "配对", send: "发到这台电脑", snote: "一次发一个文件。断掉只重发这一个。",
get: "从这台电脑取", refresh: "重新列出",
zip: "文件夹打成一个 zip，断了只能整个重取；单个文件可以从断处继续。",
saved: "已存入", renamed: "名字已被占用，存为", waiting: "等待", failed: "没发出去", retry: "重试",
count: "篮子里", isdir: "文件夹", isfile: "文件", take: "下载",
noFolder: "这个浏览器选不了文件夹，只能发文件。",
empty: "篮子是空的。在电脑窗口里把文件拖进来就会列在这里。",
errNet: "联系不上这台电脑。窗口是不是关了？",
errExpired: "二维码里的配对码已经过期，请在电脑上重新生成。",
errWrong: "这个地址回答的电脑，和二维码里的不是同一台。",
errVersion: "这台电脑的协议比本页新，请从窗口重新扫码。",
errOther: "配对没有成功：",
pair_required: "还没有配对。", bad_code: "配对码不对。", code_expired: "配对码已经过期。",
rate_limited: "试的次数太多，等一分钟再试。", device_identity_changed: "设备身份变了，请重新配对。",
declined: "电脑上拒绝了这次接收。", path_unsafe: "名字里有这台电脑不能用的字符。",
name_reserved: "这个名字是这台电脑保留的。", incomplete_body: "传输断了，文件没收完。",
verification_failed: "收到的和它声明的不一致。", bad_request: "请求不完整。",
not_found: "这一项已经不在了。", name_conflict: "那里已有同名文件，改名再发。",
path_too_long: "名字太长了。"
} : {
title: "LAN quick transfer", pair: "Pairing", hint: "The six-character code from the computer. Scanning filled it in.",
go: "Pair", send: "Send to this computer", snote: "One file per request; only the one that breaks is resent.",
get: "Take from this computer", refresh: "List again",
zip: "A folder comes as one zip, so a break means retaking it whole; a single file resumes.",
saved: "Saved as", renamed: "Name taken, saved as", waiting: "Waiting", failed: "Not sent", retry: "Retry",
count: "In the basket", isdir: "folder", isfile: "file", take: "download",
noFolder: "This browser cannot pick a folder, only files.",
empty: "The basket is empty. Drop files into the window on the computer.",
errNet: "Cannot reach this computer. Did its window close?",
errExpired: "The code in that QR has expired. Regenerate it on the computer.",
errWrong: "This address answers as a different computer than the QR named.",
errVersion: "This computer speaks a newer protocol. Scan again from the window.",
errOther: "Pairing failed: ",
pair_required: "Not paired yet.", bad_code: "Wrong code.", code_expired: "The code expired.",
rate_limited: "Too many tries. Wait a minute.", device_identity_changed: "This device's identity changed; pair again.",
declined: "Somebody at the computer refused this.", path_unsafe: "That name has characters it cannot use.",
name_reserved: "That name is reserved on that computer.", incomplete_body: "The transfer broke; the file is incomplete.",
verification_failed: "The bytes did not match their claim.", bad_request: "Incomplete request.",
not_found: "That item is gone.", name_conflict: "Taken there; rename and resend.",
path_too_long: "The name is too long."
};
function t(k) { return L[k] || k }
function el(i) { return document.getElementById(i) }
function put(i, v) { el(i).textContent = v }
document.documentElement.lang = zh ? "zh" : "en";
put("h1", t("title")); put("ph", t("pair")); put("hint", t("hint")); put("go", t("go"));
put("sh", t("send")); put("snote", t("snote")); put("gh", t("get")); put("rl", t("refresh")); put("zip", t("zip"));
var TK = "fs_token", DK = "fs_device";
var tok = sessionStorage.getItem(TK) || "";
var me = sessionStorage.getItem(DK) || "";
var sid = "";
var H = {};
location.hash.replace(/^#/, "").split("&").forEach(function (kv) {
    var i = kv.indexOf("=");
    if (i > 0) H[kv.slice(0, i)] = decodeURIComponent(kv.slice(i + 1));
});
function api(p) { return "/api/v1" + p }
// A header value has to be ASCII, so the phone names itself in a form the computer can always read; the word a
// person sees for it is that computer's own translation of the kind, not something a page writes into it.
function deviceName() {
    var p = navigator.userAgentData && navigator.userAgentData.platform;
    return p && /^[ -~]+$/.test(String(p)) ? String(p) : "phone";
}
function newId() {
    var a = crypto.getRandomValues(new Uint8Array(32)), s = "";
    for (var i = 0; i < a.length; i++) s += (a[i] < 16 ? "0" : "") + a[i].toString(16);
    me = s; sessionStorage.setItem(DK, me); return me;
}
function auth() { return tok ? { "Authorization": "FS-LAN " + me + ":" + tok } : {} }
function peerHeaders() {
    var h = auth();
    h["X-FS-Device-Name"] = deviceName();
    h["X-FS-Device-Kind"] = "phone";
    return h;
}
function why(j) { var r = j && j.reason; return L[r] ? t(r) : t("errOther") + (r || "") }
function read(x) { try { return JSON.parse(x) } catch (e) { return null } }
function size(n) {
    if (n < 1024) return n + " B";
    var u = ["KB", "MB", "GB"], i = -1, x = n;
    do { x /= 1024; i++ } while (x >= 1024 && i < 2);
    return (x < 10 ? x.toFixed(1) : Math.round(x)) + " " + u[i];
}
function bad(m) { put("err", m || "") }
function row(list, name, status) {
    var li = document.createElement("li"), b = document.createElement("b"), s = document.createElement("small"), w = document.createElement("div");
    b.textContent = name; w.className = "b"; w.appendChild(document.createElement("i"));
    li.appendChild(b); li.appendChild(s); li.appendChild(w); list.appendChild(li);
    return {
        bar: w.firstChild, list: list, li: li,
        set: function (v) { s.textContent = v },
        drop: function () { li.parentNode.removeChild(li) }
    };
}
function pair(code) {
    bad("");
    fetch(api("/pair"), {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code: code, deviceId: newId(), deviceName: deviceName(), kind: "phone" })
    }).then(function (r) {
        return r.text().then(function (x) { return { s: r.status, j: read(x) } });
    }).then(function (x) {
        if (x.s !== 201) { bad(x.j ? why(x.j) : t("errOther") + x.s); return }
        tok = x.j.token; sessionStorage.setItem(TK, tok); enter();
    }, function () { bad(t("errNet")) });
}
function enter() {
    el("pair").className = "hd"; el("send").className = ""; el("get").className = "";
    load();
}
function rows(files, isFolder) {
    var n = files.length, i = 0;
    sid = me.slice(0, 12) + "-" + Date.now().toString(36);
    // One file per request, and the next one starts only when the previous one has an answer, so a folder that
    // breaks halfway leaves exactly the files the phone already showed as sent.
    (function next() { if (i < n) send(files[i++], isFolder, i, n, next); })();
}
function send(file, isFolder, index, total, next) {
    var r = row(el("upl"), file.name, t("waiting") + " " + index + "/" + total);
    var rel = (isFolder && file.webkitRelativePath) ? file.webkitRelativePath : file.name;
    var x = new XMLHttpRequest();
    x.open("POST", api("/inbox"));
    var h = peerHeaders();
    h["X-FS-Rel"] = encodeURIComponent(rel);
    h["X-FS-Total"] = String(file.size);
    h["X-FS-Mtime"] = String(file.lastModified || 0);
    h["X-FS-Session"] = sid;
    for (var k in h) x.setRequestHeader(k, h[k]);
    x.upload.onprogress = function (e) {
        if (e.lengthComputable) r.bar.style.width = Math.round(e.loaded * 100 / e.total) + "%";
    };
    function done(message, retry) {
        r.set(message);
        if (!retry) { next(); return }
        var btn = document.createElement("button");
        btn.className = "g"; btn.textContent = t("retry");
        btn.onclick = function () { r.drop(); send(file, isFolder, 1, 1, function () { }) };
        r.li.appendChild(btn);
        next();
    }
    x.onload = function () {
        if (x.status >= 200 && x.status < 300) {
            var j = read(x.responseText) || {};
            done(t(j.renamed ? "renamed" : "saved") + " " + (j.savedAs || file.name), false);
        } else {
            var j2 = read(x.responseText);
            done(t("failed") + "：" + (j2 ? why(j2) : x.status), true);
        }
    };
    x.onerror = function () { done(t("failed") + "：" + t("errNet"), true) };
    x.send(file);
}
function load() {
    var h = peerHeaders();
    fetch(api("/basket"), { headers: h }).then(function (r) {
        return r.text().then(function (x) { return { s: r.status, j: read(x) } });
    }).then(function (x) {
        var list = el("dwl");
        list.textContent = "";
        if (x.s !== 200 || !x.j || !x.j.length) {
            put("cnt", x.s === 200 ? "" : why(x.j || {}));
            var li = document.createElement("li"), s = document.createElement("small");
            s.textContent = t("empty"); li.appendChild(s); list.appendChild(li);
            return;
        }
        put("cnt", t("count") + " " + x.j.length);
        x.j.forEach(function (it) {
            var li = document.createElement("li"), b = document.createElement("b"), s = document.createElement("small"), a = document.createElement("a");
            b.textContent = it.name;
            s.textContent = " " + size(it.bytes) + " · " + t(it.isDirectory ? "isdir" : "isfile") + " ";
            a.href = api("/basket/" + encodeURIComponent(it.id) + (it.isDirectory ? "/zip" : ""));
            a.download = it.isDirectory ? it.name + ".zip" : it.name;
            a.textContent = t("take");
            li.appendChild(b); li.appendChild(s); li.appendChild(a); list.appendChild(li);
        });
    }, function () { put("cnt", t("errNet")) });
}
el("go").onclick = function () { pair(el("code").value.toUpperCase()) };
el("rl").onclick = load;
// A chosen file list is copied before the input is cleared, because emptying the input is the only way to let
// the same file be picked again and a browser may empty the live list out from under a transfer in progress.
el("f").onchange = function () { rows([].slice.call(this.files), false); this.value = "" };
el("d").onchange = function () {
    var files = [].slice.call(this.files);
    this.value = "";
    if (files.length && !files[0].webkitRelativePath) { put("snote", t("noFolder")); return }
    put("snote", t("snote")); rows(files, true);
};
fetch(api("/hello")).then(function (r) { return r.json() }).then(function (h) {
    put("who", (h.deviceName || "") + " · " + location.host);
    if (h.v !== 1) { bad(t("errVersion")); return }
    if (H.d && H.d !== h.deviceId) { bad(t("errWrong")); return }
    if (tok) { enter(); return }
    if (H.k) el("code").value = H.k;
    if (h.pairingArmed && H.k && (!H.e || (+H.e) * 1000 > Date.now())) pair(H.k);
    else if (H.k) bad(t("errExpired"));
}, function () { bad(t("errNet")) });
})();
</script>
</body>
</html>

""";
}
