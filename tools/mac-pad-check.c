// Proves the Mac "Gamepad in mode 0" switch on real hardware: reads the layout
// the switch left in launchd, the way a game opened from Finder gets it, then
// says whether SDL takes the plugged-in QuadStick as a gamepad and prints each
// button and stick as it moves.
//
//   brew install sdl3
//   clang tools/mac-pad-check.c -o /tmp/mac-pad-check $(pkg-config --cflags --libs sdl3) && /tmp/mac-pad-check
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>

int main(void)
{
    // A Terminal opened before the switch was flipped does not have the
    // variable, so take it from launchd itself.
    static char layout[8192];
    FILE *p = popen("launchctl getenv SDL_GAMECONTROLLERCONFIG", "r");
    size_t n = p ? fread(layout, 1, sizeof layout - 1, p) : 0;
    if (p) pclose(p);
    layout[n] = 0;
    while (n && layout[n - 1] == '\n') layout[--n] = 0;
    if (!n) { puts("FAIL: the switch is off. Turn on \"Gamepad in mode 0\" in the app first."); return 1; }
    SDL_setenv_unsafe("SDL_GAMECONTROLLERCONFIG", layout, 1);

    if (!SDL_Init(SDL_INIT_GAMEPAD)) { printf("FAIL: SDL did not start: %s\n", SDL_GetError()); return 1; }
    SDL_Delay(1000);
    printf("SDL %d.%d.%d\n", SDL_VERSIONNUM_MAJOR(SDL_GetVersion()), SDL_VERSIONNUM_MINOR(SDL_GetVersion()), SDL_VERSIONNUM_MICRO(SDL_GetVersion()));

    int count = 0;
    SDL_JoystickID *ids = SDL_GetJoysticks(&count);
    SDL_Gamepad *pad = NULL;
    for (int i = 0; i < count; i++)
    {
        char guid[33];
        SDL_GUIDToString(SDL_GetJoystickGUIDForID(ids[i]), guid, sizeof guid);
        bool quadstick = SDL_GetJoystickVendorForID(ids[i]) == 0x16D0 && SDL_GetJoystickProductForID(ids[i]) == 0x092B;
        printf("%s  %04x:%04x  %s  %s\n", guid, SDL_GetJoystickVendorForID(ids[i]), SDL_GetJoystickProductForID(ids[i]),
            SDL_IsGamepad(ids[i]) ? "gamepad" : "not a gamepad", SDL_GetJoystickNameForID(ids[i]));
        if (quadstick && SDL_IsGamepad(ids[i])) pad = SDL_OpenGamepad(ids[i]);
        else if (quadstick) puts("FAIL: SDL sees the QuadStick but not as a gamepad. Send this whole output to the developer.");
    }
    SDL_free(ids);
    if (!pad) { puts("FAIL: no QuadStick in mode 0 found. Plug it in with a mode 0 profile and run this again."); return 1; }

    puts("PASS: games built on SDL will see the QuadStick as a gamepad.\n"
         "Now sip, puff and move the stick for 30 seconds. Each one should print the button you meant.");
    Uint64 end = SDL_GetTicks() + 30000;
    SDL_Event e;
    while (SDL_GetTicks() < end)
        while (SDL_WaitEventTimeout(&e, 100))
        {
            if (e.type == SDL_EVENT_GAMEPAD_BUTTON_DOWN)
                printf("button %s\n", SDL_GetGamepadStringForButton(e.gbutton.button));
            else if (e.type == SDL_EVENT_GAMEPAD_AXIS_MOTION && (e.gaxis.value > 16000 || e.gaxis.value < -16000))
                printf("stick %s %s\n", SDL_GetGamepadStringForAxis(e.gaxis.axis), e.gaxis.value > 0 ? "+" : "-");
        }
    SDL_CloseGamepad(pad);
    SDL_Quit();
    return 0;
}
