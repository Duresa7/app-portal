# App Portal

A self-service software portal a company runs for itself. Staff install approved software on their own PCs without raising a ticket, and administrators decide what is on offer, who may ask for it, and what happened.

## Language

### Software on offer

**Catalog app**:
A piece of software an administrator has published for staff to install. A hidden catalog app stays in the catalog but is not offered to anyone.
_Avoid_: package, application, program, title, product

**Engine**:
The mechanism that carries out an install on a device. Exactly two exist, written lower case everywhere: `action1` and `agent`.
_Avoid_: provider, backend, installer, driver, runner

**Install**:
One attempt to put one catalog app on one device, from the moment it is asked for until it succeeds or fails. It survives as history afterwards, including when the device is retired.
_Avoid_: deployment, job, installation, run

**Request**:
A staff member asking, in free text, for software that is not in the catalog. An administrator approves or denies it with a reason, and the asker sees both. A request never becomes a link to a catalog app.
_Avoid_: ticket, suggestion, ask, proposal

**Scope**:
Who an install is for. A machine-wide install serves everyone on the device and runs as SYSTEM. A per-user install serves one requester, lands in their profile, and runs in their session.
_Avoid_: context, target, per-machine, all-users, level

**Prerequisite**:
A catalog app that must be installed before another one. The portal installs the chain in order as one install.
_Avoid_: dependency, requirement, bundle, parent

**Requirement**:
Something a catalog app needs that the portal cannot arrange, written in plain words for the person to read and confirm before installing, such as Secure Boot or a vendor account. The portal never checks one and never refuses an install over one.
_Avoid_: prerequisite, capability, constraint, spec, check

### People and machines

**Device**:
A managed PC the portal knows about. A retired device can no longer be used, but everything it did is kept.
_Avoid_: machine, endpoint, PC, computer, host, node

**Fleet**:
Every device the portal knows about, taken together, retired ones included.
_Avoid_: estate, inventory, park, population

**Requester**:
The signed-in Windows account that asked for an install or a request. The portal trusts it because the device is managed rather than because the account proved anything.
_Avoid_: user, staff member, employee, owner

**Administrator**:
Someone who may sign in to the administration surfaces. Their account is either local to the portal or provisioned from a directory the first time they sign in; local accounts are always checked first.
_Avoid_: operator, superuser, staff, manager

**Enrollment key**:
A credential an administrator hands out so that a device may join the fleet. It can be revoked, and its use is recorded.
_Avoid_: licence key, token, invite code, join code
