// What the modules share. `shared` is what is shown: the last data of /api/today, whether it is loading, the view of
// the address and the page it names (a PBI or a pull request with its tab), the description folded or not, and the
// language of the files shown (null: the language of the page).
export const shared = { lastData: null, loading: false, activeView: "today", pbiRoute: null, prRoute: null, descOpen: false, docLang: null };
// What a view asks of the page without knowing it: main.js puts its own functions here when it starts.
export const hooks = { load() { }, render() { }, renderPbi() { } };
