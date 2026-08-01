# Privacy Policy

Quadstick: Config Manager has no accounts and no advertising. It runs on your
computer and works with the profile files you choose. Nothing about you or your
profiles is sold or shared with anyone.

## What the app can send, and when

Four things use the network, and each one only after you ask for it.

**Google Sheets and Drive.** When you paste a Sheets link, the app fetches that
sheet and nothing else. When you turn on Google Drive backup, it saves your
profiles to your own Drive under your own account.

**The community profile list.** When you open Community profiles, the app asks
quadstick.com for the shared list of game profiles, and Refresh in that window
asks again. That is the only time it is fetched: opening the app and using the
home screen never ask for it. The request carries nothing about you or your
profiles, and the last list is saved on your computer so the window still opens
without internet.

**Usage data, off unless you turn it on.** The first time you open the app it
asks. If you say yes, it sends a small event when the app opens, when a profile
is opened or saved, when installing to a QuadStick is tried and whether that
worked, and when one of the optional features is used. Each event carries your
operating system, the app version, and a random install ID. While this is on,
those events are sent as they happen. You can turn it off any time in Settings,
under Advanced.

**Crash reports, one at a time.** If the app crashes it saves a report on your
computer and sends nothing. The next time you open the app it shows you that
report and asks. Nothing leaves your computer unless you press Send.

## What is never sent

Your profiles and their contents. File names and file paths. Anything you type,
apart from the feedback box, which is only sent when you press Send on it. Your
Google account, tokens, sheet IDs, or share links. Your name, your user name, or
your computer's name. A crash report carries the type of error and the list of
functions it happened in, never the error message, because a message can quote
your own data back.

## The install ID

A random identifier made once and stored on your computer. It is not derived
from anything about you or your machine, and it is only made when you first turn
usage data on or press Send on a crash report. It exists so that a count of
installs is a real number rather than a count of launches, and so a deletion
request can find your data. You can see it and copy it in Settings, under
Advanced.

## Who processes it

Usage data and crash reports are processed by PostHog on servers in the United
States. The reporting library also attaches its own name and version, and tells
PostHog not to work out a location from your IP address. Every request carries
the library version, the .NET version, your operating system version, and
whether the processor is Intel or ARM. If you are outside the United States,
this means the data is stored outside your country.

## Turning it off

Settings, then Advanced. Turning off usage data stops it immediately. Turning
off crash reports also deletes any report waiting on your computer, and so does
resetting settings.

Setting the environment variable `QSCM_TELEMETRY=0` before the app starts means
nothing is ever sent and you are never asked. A crash still writes its report to
your own computer, the same as the crash log the app has always kept, and with
that variable set nothing ever offers to send it. A build made from source sends
nothing at all.

## Deleting your data

Open an issue with your install ID and it will be deleted.

Questions? Open an issue at
https://github.com/Bbrizly/Quadstick-Config-Manager/issues.
