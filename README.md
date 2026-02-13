# NvidiaDriverTrayChecker

Checks for new versions of Nvidia GeForce graphics drivers.
If you like, it will download and install.

## Purpose

I created this because I don't like Nvidia Experience and the
website makes you jump through hoops to check for new versions.

This was ported from node because I got tired of having to
update packages constantly in node hell.

## Usage

Build the project. If you have Visual Studio, publish the project.
If you have VSCode, run the ps.

This app runs with a tray icon as its interface. When it runs,
it will check the NVidia website for driver versions, compare to
your installed version, and if there is a new version, put
up a notification, which doesn't always show up. If the tray icon has
a red dot in it, there's a new version. If you right click on the tray
icon, you can download and install the new drivers, or you can
exit the app. If you start downloading, you can cancel from the menu.


## Game Ready Drivers vs Studio Drivers

This defaults to downloading Studio drivers. Right now there'a lines
at the top of the `Program.cs` file that you can comment to change the
url to Game Ready. I could add a config sheet to the app, but I feel
like no one will be interested in this except me. Who knows.

## Compatibility

This only works on Windows 10+ as far as I know. I'm using Windows 11.

## Future

I'm planning to also make a super straightforward console app version
of this at some point. Something like run, hey which driver (Studio or
GameReady), hey cool there's a new one wanna install? Hey cool.



