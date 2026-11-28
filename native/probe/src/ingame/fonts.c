// Nunito (SIL Open Font License 1.1, third_party/fonts/Nunito-LICENSE.txt), the MKWMP launcher's own font, embedded in
// the mod so the in-game overlay looks the same on every system (a Proton prefix has no fonts of its own). C23 #embed:
// the paths are relative to this file.
#include <stddef.h>

const unsigned char mkwmp_font_regular[] = {
#embed "../../third_party/fonts/Nunito-Regular.ttf"
};
const unsigned char mkwmp_font_semibold[] = {
#embed "../../third_party/fonts/Nunito-SemiBold.ttf"
};
const unsigned char mkwmp_font_bold[] = {
#embed "../../third_party/fonts/Nunito-Bold.ttf"
};
const unsigned char mkwmp_font_extrabold[] = {
#embed "../../third_party/fonts/Nunito-ExtraBold.ttf"
};
const size_t mkwmp_font_regular_size = sizeof mkwmp_font_regular;
const size_t mkwmp_font_semibold_size = sizeof mkwmp_font_semibold;
const size_t mkwmp_font_bold_size = sizeof mkwmp_font_bold;
const size_t mkwmp_font_extrabold_size = sizeof mkwmp_font_extrabold;
