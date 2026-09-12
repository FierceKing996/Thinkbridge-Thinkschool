# shared/

Reusable, presentational building blocks that are **feature-agnostic** and have
no dependency on `core/` services or on any one `features/` area — dumb UI
components, pipes, directives, and small pure helpers.

Nothing lives here yet. Add a folder per item as the need arises, e.g.
`shared/ui/spinner/`, `shared/pipes/relative-time.ts`.

Rules of thumb:

- If it talks to the backend, holds app state, or is a singleton service →
  it belongs in `core/`.
- If it only makes sense inside one screen → keep it next to that screen in
  `features/<area>/<screen>/`.
- If two or more features would import it and it renders or transforms data
  without knowing where the data came from → it belongs here.
