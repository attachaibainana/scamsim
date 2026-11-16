// Browser helpers for the WebGL build. Called from ScamSim.Platform.WebBridge.
mergeInto(LibraryManager.library, {

  // Session logs are written to Application.persistentDataPath, which lives in an in-memory file
  // system backed by IndexedDB. Flush it after every save so a closed or reloaded tab keeps the log.
  // Saves can come back to back; never run two flushes at once, run one more after the current one.
  ScamSim_SyncFs: function () {
    if (typeof FS === "undefined" || !FS.syncfs) return;
    var state = Module.scamSimSync || (Module.scamSimSync = { running: false, again: false });
    if (state.running) {
      state.again = true;
      return;
    }
    var run = function () {
      state.running = true;
      state.again = false;
      FS.syncfs(false, function (err) {
        state.running = false;
        if (err) console.warn("[ScamSim] Could not save the session log to browser storage:", err);
        if (state.again) run();
      });
    };
    run();
  },

  // Hands bytes to the browser as a file download (Export CSV). Returns 1 when the download started.
  ScamSim_Download: function (namePtr, mimePtr, base64Ptr) {
    try {
      var name = UTF8ToString(namePtr);
      var mime = UTF8ToString(mimePtr);
      var binary = atob(UTF8ToString(base64Ptr));
      var bytes = new Uint8Array(binary.length);
      for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

      var url = URL.createObjectURL(new Blob([bytes], { type: mime }));
      var link = document.createElement("a");
      link.href = url;
      link.download = name;
      link.style.display = "none";
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
      return 1;
    } catch (e) {
      console.warn("[ScamSim] Download failed:", e);
      return 0;
    }
  }
});
