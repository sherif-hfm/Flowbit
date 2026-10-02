  (() => {
    const storageKey = "flowbit.theme";
    let theme = null;
    try {
      theme = localStorage.getItem(storageKey);
    } catch (err) {}
    if (theme !== "light" && theme !== "dark") {
      const prefersDark = typeof window.matchMedia === "function"
        ? window.matchMedia("(prefers-color-scheme: dark)").matches
        : true;
      theme = prefersDark ? "dark" : "light";
    }
    document.documentElement.dataset.theme = theme;
    document.getElementById("themeColorMeta")
      ?.setAttribute("content", theme === "dark" ? "#0f172a" : "#f4f7fb");
  })();
