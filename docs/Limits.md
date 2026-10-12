# Limits

* **Looked at in pictures by the mod's author alone**, on 2026-10-08: the launch pad by day, at sunrise,
  at sunset and by night, a night launch, and low orbit over Kerbin and Duna. Nobody has flown a mission
  with it.
* **The film look needs the TUFX mod**, and a TUFX profile that keeps more than white in the picture
  (TUFX's own do). It is a layer over that profile: one with a film curve or heavy colour grading of its
  own will be doubled.
* **Only what is near keeps more than white.** The game draws the far scene (beyond 400 metres) and the
  planets with other cameras, and what they draw is cut off at white before the film ever sees it. So a
  flame and a floodlit rocket glow as they should, and a far snowfield or the sun's own disc only as much
  as white can.
* **The shafts of sunlight are made from the picture, not from the air.** Only what is in the picture can
  throw one: with the sun just outside it they come from the bright sky at the edge alone. Only what is
  near (within the few hundred metres the game's near camera draws) is known to be in front of the sun;
  a building two kilometres off or a mountain hides the sun only by being dark in the picture, and some
  of the glow lies over it. A cloud does not throw a shaft unless it is darker than the sky round it.
  With Scatterer's own "godrays" turned on as well there would be two sets: use one.
* **The shafts**, the veil and the blacking out of the game's pixels that are not numbers (see
  [how it works](How-it-works.md)) are shaders packed for the game's OpenGL and built for Direct3D. The
  three were seen in use on Windows (2026-10-10). None has been run on Linux.
* **The veil of weather is the weather where the camera is**, the same all round it: a shower a few kilometres
  off does not grey that part of the view more than the rest (the weather mod's clouds draw the shower). It is
  veiled by what is behind each pixel: on the far camera a see-through thing (smoke, a flame, a cloud) is
  veiled as if it were as far off as what is behind it, and on the near one not at all. Lamps light the drops
  near the camera (the weather mod's doing) but not the veil: a floodlight in fog has no glow round it in the
  air. Whether another mod's clouds are drawn before the veil (veiled, as they should be when the rain is
  between them and the camera) or after it depends on the order the two are set going in. On a Mac and on
  Linux only, so far, like the shafts.
* **The camera's eye is one number for the whole view**, measured three to eight times a second: it can
  pump a little when something bright crosses the view. What it finds "bright" is a few dots of a picture
  shrunk to 80 by 45, so a thing smaller than a dot or two (a lamp far off) does not hold it back.
* **Pushed three stops, the far scene shows steps.** The game hands the far scene and the planets to the
  film already rounded to 256 levels; multiplied by nine, the levels show in a dark sky or on dark ground.
  The grain is there partly to hide them.
* **In the editors there is the film look alone**, at one exposure: the lights and the eye are for flight
  and the space centre. The assembly building and the hangar keep their own lamps.
* **The film look was set on seven views** (the pad by day, at sunset and by night under the floodlights, a
  launch, the space centre, a rocket in the sun, low orbit over Kerbin), by eye and by measuring how strong its
  colours come out against the old look and, by day, at sunset and by night, against the game's own picture.
  Other places and other planets have not been looked at with it since it was changed in 0.9.0.
* **The rays are one set, from the middle of the craft**, sixty-four of them: they tell a craft in the open
  from one by a wall. What they find lights everything in the picture alike, so ground far from the wall
  is tinted by it too; a thing thinner than the gap between two rays is missed; the craft's own parts are
  not counted as hiding the sky; and no lamp's light is traced, only the sun's and the sky's.
* **The light of the runway's lamps and of the windows is drawn on the ground**, not cast: a soft patch of
  light laid on it. So nothing standing in it throws a shadow across it (a craft on the runway does not
  darken the lamp's pool behind it), it is the same on grass as on concrete but for the colour under it, and
  the white of the runway's markings is lit no more than the concrete beside them. What stands in the light is
  lit by real lamps of the cheap kind, a few of them, near the craft being flown and in front of the brightest
  windows.
* **The windows are found on the space centre's own buildings**, from their meshes and the pictures that say
  where they glow. A building some other mod has put there, whose meshes cannot be read, is lit by rule as
  before 0.8.0: light along the foot of every wall the rays find, lit or not.
* **Only lamps whose meshes are named as lamps** ("light" in the name, as the runway's are) are found. The
  helipads on the assembly building's roof and the red lamps on its corners light nothing.
* **The deep sky is 1024 dots to a face**, softer than a 4096 sky under it: its nebulae are soft things and
  its stars are few, but drawn in close a star of its own is a blob. It was looked at from Kerbin's orbit
  only, with one number; others have not been looked at one by one.
* **Nights are dark.** The game keeps its flat 0.24 in air at midnight; this keeps 0.045 unless told
  otherwise, plus what the Mun gives. Landing by night away from a launch site, without lights and with
  no Mun, is correspondingly harder.
* **No lamp throws a shadow.** The game's shaders have shadows for the sun and for nothing else: an
  engine's light and a floodlight shine through whatever stands in the way.
* **The cheap lamps are coarse.** They are worked out at the corners of what they light. On a large flat
  thing with few corners (a wall of the assembly building) a pool of light is smeared out or missed.
* **Smoke takes one light for each source.** All the smoke from one engine, or one vent of the launch
  pad, is tinted alike by the light where it comes from: smoke that has drifted a long way from a flame is
  still lit as if beside it, and no smoke is in shadow. Only the game's own smoke: a mod that draws smoke
  with a shader of its own (Waterfall's plumes, the Volumetric Explosions mod, which has its own light) is
  left alone.
* **The floodlights are one searchlight each, the same at every site**, four to a site, turned over its middle
  (round a pad) or across it (beside a runway), not at the craft; each beam is a flat strip turned to the
  camera (seen from straight along its length it is thin). Where a site is a runway is told from the shape of
  its surface: a runway some mod has made whose surface is not part of its launch site is taken for a pad, and
  its floodlights may stand on it (they have no colliders, so nothing can hit them). They do not turn to follow
  a craft, and nothing of theirs can be switched on or moved by hand. Round a pad the four stand on the
  compass's diagonals, which need not be lines of the pad's own, as far out as all four can stand alike: on the
  small top of the space centre's pad that is by the corners of its grate, twelve metres from the rocket (where
  two of the four stood before): close for a very wide rocket, which goes through them without harm.
  0.10.3's changes to them (one frame for a site's searchlights, the four placed together, the way a runway
  runs) were seen on a Mac at the space centre's pad, by day and by night, and beside its runway by night: at
  no other site.
* **A low sun behind mountains.** West of the Kerbal Space Centre the mountains hide the evening sun from
  about eight degrees up, and their shadow falls on the pad: the golden light is over earlier there in
  the evening than at sunrise, which comes up out of the sea. That is the game's doing, and right.
* The sky is one colour. A real sky is brightest round the sun and whitest at the horizon.
* The ground is one colour from below. A white wall beside the craft throws no light on it.
* The ground's colour is the planet's picture as seen from afar. Near the ground it knows land from water
  but not a launch pad from the grass round it, nor snow on a mountain the picture is too coarse to show.
* Clouds are a guess: a body with air is taken to throw back a sixth of the sunlight from its air and
  clouds, whether or not a cloud mod is installed and whatever the weather under the craft.
* An eclipse by another body (the Mun over Kerbin) dims nothing here (Scatterer's eclipses still dim the
  sun itself).
* It is the light for the craft being flown. Another craft far away in the same scene gets the same sky
  and ground.
* **With PlanetShine**, or any other mod that sets the game's ambient light, the two would fight: use one.
  With the Engine Lighting mod installed, this mod leaves engines to it.
* **Windows:** tried on Windows 11 (KSP 1.12.5, Direct3D 11) without TUFX, Scatterer or EVE: the lights of
  the space centre at night (the floodlights, the runway's lamps, the windows' light), the deep sky, the
  shafts (above); then with TUFX the film look, and with Natural Weather the veil; later the engines' light
  at night and the cabin by day and by night.
* **Linux:** but for the shaders above, nothing in it belongs to one system; it has not been run there.
