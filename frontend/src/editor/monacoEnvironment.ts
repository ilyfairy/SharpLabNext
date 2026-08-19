import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker'

interface MonacoRuntimeGlobal {
  MonacoEnvironment?: {
    getWorker: (_moduleId: string, _label: string) => Worker
  }
}

const monacoRuntime = globalThis as typeof globalThis & MonacoRuntimeGlobal

monacoRuntime.MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
}
