// Akasha Monaco editor — JS interop
window.akashaMonaco = {
  _configured: false,
  create: function (containerId, initialValue, dotnetRef) {
    if (!window.require) return;
    if (!window.akashaMonaco._configured) {
      require.config({ paths: { vs: 'lib/monaco/min/vs' } });
      window.akashaMonaco._configured = true;
    }
    require(['vs/editor/editor.main'], function () {
      var el = document.getElementById(containerId);
      if (!el) return;
      if (window.__akashaMonacoEditor) { window.__akashaMonacoEditor.dispose(); }
      var editor = monaco.editor.create(el, {
        value: initialValue || '',
        language: 'json',
        theme: 'vs-dark',
        automaticLayout: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        fontSize: 13
      });
      editor.onDidChangeModelContent(function () {
        dotnetRef.invokeMethodAsync('OnEditorChanged', editor.getValue());
      });
      window.__akashaMonacoEditor = editor;
    });
  }
};
