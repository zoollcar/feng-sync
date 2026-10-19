import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import { isChinese } from "./i18n/strings";
import "./styles.css";

// The document declares the language it is written in, so a screen reader and a browser's own translation prompt
// both agree with the words on screen.
document.documentElement.lang = isChinese ? "zh" : "en";

const root = document.getElementById("root");
if (root) {
  createRoot(root).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}
