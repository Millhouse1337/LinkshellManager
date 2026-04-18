# Web and Discord Activity Parity Checklist

This checklist defines parity as:

- same data model
- same allowed options
- same actions available to the user
- same permission rules
- same outcomes

The only intentionally different part is the login entry surface.

## Already aligned

- Discord-linked identity resolves to the same `AppUser` on web and in the Activity.
- MVC and Activity now enforce the same membership, manager, and leader permission rules for linkshells, events, dashboard selection, and event history access.
- Linkshell creation, edit, delete, leave, primary selection, member removal, and role updates exist in both surfaces.
- Invite send, accept, decline, revoke, join requests, and join-request approval/decline exist in both surfaces.
- Event create, edit, cancel, start, signup, unsign, verification, loot, and end-event flows exist in both surfaces.
- Event job options are now fixed dropdowns in both the MVC event form and the Discord Activity queue form.
- Profile identity fields use the same underlying `AppUser` data in both surfaces.
- Timezone handling is based on UTC storage with per-user display conversion.
- Dashboard content parity is in place: linkshell selector, member list, upcoming events, and recent history are now exposed in the Activity.
- Event history parity is in place: full history list and detail views now exist in the Activity.
- Live event parity is in place on actions and data: quick join, attendance actions, loot, and end-event flow exist in both surfaces.
- Linkshell details parity is in place: the Activity now supports drill-in details for any membership, not only the primary linkshell.

## Remaining parity gaps

### 1. Invite and membership UX parity

Discord Activity has extra Discord-native affordances:

- connected participant invite shortcuts
- connected participant list
- member mode / manager mode view split
- refresh activity button

Web does not have equivalents for these.

Parity target:

- either add equivalent operational shortcuts on the web
- or accept these as Discord-only operational enhancements and document them as intentional exceptions

Priority: Medium

### 2. Profile/settings UX parity

Web has:

- dedicated profile page
- dedicated settings page

Discord Activity has:

- inline operator profile card
- inline onboarding checklist

Parity target:

- align editable fields and validation rules exactly
- decide whether onboarding is Activity-only or should be visible on the web too

Priority: Medium

### 3. Presentation-only flow differences

The underlying data, actions, and rules are now mostly aligned, but the surfaces still present them differently:

- web uses dedicated pages for dashboard, history, linkshell details, and live event operations
- Activity uses the command-deck layout with inline panels and drawers

If strict parity means identical actions and outcomes, this is acceptable. If strict parity also means matching navigation structure, more redesign work is still required.

Priority: Low

## Recommended implementation order

1. Profile/settings parity
2. Decide whether Discord-native operational affordances remain intentional exceptions
3. Decide whether page-vs-panel presentation differences matter for your definition of parity

## Intentional exceptions to decide explicitly

These features may remain Discord-only if treated as platform-specific enhancements rather than parity failures:

- connected participant list
- connected-now invite shortcuts
- runtime/session context panel
- refresh activity button

If these remain Discord-only, document them as approved exceptions instead of leaving them as accidental drift.
