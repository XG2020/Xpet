"""Local derived repairs for the supplied whale body's AI mesh contacts.

The original GLB and its textures are never written. Only the two documented
profiles are touched; real limbs, head and tail retain their own drivers.
"""
from collections import defaultdict
import math


def _weights(vertex):
    return {b: w for b, w in zip(vertex[8:12], vertex[12:16]) if b >= 0 and w > 1e-8}


def _replace(vertex, values):
    values = sorted(((b, w) for b, w in values.items() if w > 1e-8), key=lambda p: p[1], reverse=True)[:4]
    total = sum(w for b, w in values)
    if total <= 0:
        raise ValueError('身体蒙皮修复没有可用骨骼。')
    values = [(b, w / total) for b, w in values] + [(-1, 0.)] * (4 - len(values))
    return tuple(vertex[:8]) + tuple(b for b, w in values) + tuple(w for b, w in values)


def _smooth(t):
    t = max(0., min(1., t))
    return t * t * (3 - 2 * t)


def _segment_distance(p, a, b):
    delta = tuple(b[k] - a[k] for k in range(3))
    length = sum(v * v for v in delta)
    t = max(0., min(1., sum((p[k] - a[k]) * delta[k] for k in range(3)) / max(length, 1e-12)))
    return math.dist(p, tuple(a[k] + delta[k] * t for k in range(3)))


def adapt_whale_body(model):
    """Run once after the hand/sleeve repair; accepts its derived vertices.

    A garment/limb contact is split into coplanar barycentric patches with
    independent edge copies. This preserves bind coverage, normal/UV and
    linear morph offsets while preventing a skirt triangle spanning a leg.
    The child's true hip attachment is blended instead of cut.
    """
    metadata = model.get('metadata', {})
    profile = metadata.get('rig_profile', '')
    if profile not in ('whale-adult', 'whale-child-basic-v1'):
        return
    if metadata.get('body_topology_adaptation_revision'):
        return
    child = profile == 'whale-child-basic-v1'
    vertices, bones = model['vertices'], model['bones']
    names = [b['name'] for b in bones]
    lookup = {n: i for i, n in enumerate(names)}
    chains = {s: tuple(lookup[n + s] for n in ('足.', 'ひざ.', '足首.', 'つま先.')) if child
              else tuple(lookup[n + s] for n in ('thigh.', 'shin.', 'foot.')) for s in ('L', 'R')}
    all_leg = set().union(*(set(v) for v in chains.values()))
    heads = {lookup[n] for n in ('頭', '首') if n in lookup} if child else {lookup[n] for n in ('head', 'neck')}
    tails = {i for i, n in enumerate(names) if n.startswith('tail.')}
    pelvis = lookup['下半身' if child else 'pelvis']
    height = float(metadata['target_height_m'])
    surfaces = model.get('_surface_labels', {})
    hand_vertices = set().union(*model.get('_hand_masks', {}).values()) if model.get('_hand_masks') else set()
    body_material = None
    start = 0
    for material in model['materials']:
        ids = set(model['indices'][start:start + material['count']]); start += material['count']
        if 'skin_full' in material['name'].lower() or 'skin_v3' in material['name'].lower():
            body_material, selected = material, ids
            break
    if body_material is None:
        return
    original = [_weights(v) for v in vertices]
    from runtime_rig_adaptation import _png_rgb
    image = _png_rgb(body_material.get('texture'))
    colors = {}
    if image is not None:
        width, image_height, channels, pixels = image
        for i in selected:
            v = vertices[i]
            x = max(0, min(width - 1, round(v[6] * (width - 1))))
            y = max(0, min(image_height - 1, round((1 - v[7]) * (image_height - 1))))
            offset = (y * width + x) * channels
            colors[i] = tuple(pixels[offset:offset + 3])
    hair_repaired = 0
    local_hair_shadow_repaired = 0
    if not child and colors:
        # These remaining seams are blue hair strands, not garment contacts:
        # their source torso-driven half shares the head half's blue texture.
        # Grow from actual head hair over those same-color strands, excluding
        # authored tail drivers and all already repaired arm surfaces.
        neck_y = bones[lookup['neck']]['position'][1]
        allowed = {i for i in selected if surfaces.get(i, 0) == 0 and i not in hand_vertices
                   and vertices[i][1] > neck_y - height * .17
                   and sum(w for b, w in original[i].items() if b in tails) < .2
                   and colors[i][1] > 40 and colors[i][1] - colors[i][0] >= 6
                   and colors[i][2] - colors[i][1] >= 10}
        adjacency = defaultdict(set)
        first = 0
        for material in model['materials']:
            indices = model['indices'][first:first + material['count']]; first += material['count']
            if material is body_material:
                for at in range(0, len(indices), 3):
                    a, b, c = indices[at:at + 3]
                    for u, v in ((a, b), (b, c), (c, a)):
                        if u in allowed and v in allowed:
                            adjacency[u].add(v); adjacency[v].add(u)
                break
        reached = {i for i in allowed if sum(w for b, w in original[i].items() if b in heads) > .5}
        pending = list(reached)
        while pending:
            i = pending.pop()
            for other in adjacency[i] - reached:
                reached.add(other); pending.append(other)
        template = {lookup['head']: .85, lookup['chest']: .15}
        for i in reached:
            # Existing head/neck skin is already driven correctly. Only repair
            # the torso-driven blue strand half; do not change scalp/face motion.
            if sum(w for b, w in original[i].items() if b in heads) > .5:
                continue
            replacement = _replace(vertices[i], template)
            if replacement[8:] != vertices[i][8:]:
                vertices[i] = replacement; original[i] = _weights(replacement); hair_repaired += 1
        # Verified small shadow islands miss the strict blue flood. Complete
        # their local same-UV blue/dark-blue component to its stable head ring,
        # instead of painting one endpoint and moving a hard seam to its neighbor.
        shadows = ((.046987329, 1.20359695, .139466053),
                   (-.332570374, 1.19191706, .041517504),
                   (-.196075499, 1.21017742, .143106788),
                   (-.195624366, 1.20412672, .148270786))
        # A long source triangle reaches one verified same-UV outer endpoint
        # just outside the 2 cm box. Include that endpoint, not a larger region.
        outer_endpoint = (.034308415, 1.22785332, .141432524)
        patch = {i for i in selected if surfaces.get(i, 0) == 0 and i not in hand_vertices
                 and sum(w for b, w in original[i].items() if b in tails) < .2
                 and (any(math.dist(vertices[i][:3], shadow) < .020 for shadow in shadows)
                      or math.dist(vertices[i][:3], outer_endpoint) < .0001)
                 and colors[i][2] - max(colors[i][0], colors[i][1]) >= 8}
        shadow_nodes = {i for i in patch if sum(w for b, w in original[i].items() if b in heads) < .5}
        seeds = {i for i in shadow_nodes if (any(math.dist(vertices[i][:3], shadow) < .0001 for shadow in shadows)
                                             or math.dist(vertices[i][:3], outer_endpoint) < .0001)}
        local_edges = defaultdict(set)
        first = 0
        for material in model['materials']:
            indices = model['indices'][first:first + material['count']]; first += material['count']
            if material is body_material:
                for at in range(0, len(indices), 3):
                    tri = indices[at:at + 3]
                    for i in tri:
                        if i in patch:
                            local_edges[i].update(j for j in tri if j != i)
                break
        def same_uv(i, j):
            return max(abs(vertices[i][6] - vertices[j][6]), abs(vertices[i][7] - vertices[j][7])) < .025
        welded = defaultdict(list)
        for i in patch:
            key = tuple(round(v * 1000000) for v in vertices[i][:3]); welded[key].append(i)
        for ids in welded.values():
            for i in ids:
                local_edges[i].update(j for j in ids if j != i and same_uv(i, j))
        complete = set(seeds); pending = list(seeds)
        while pending:
            i = pending.pop()
            for other in local_edges[i]:
                if other in shadow_nodes and other not in complete and same_uv(i, other):
                    complete.add(other); pending.append(other)
        stable_ring = set()
        unfinished = set()
        for i in complete:
            for other in local_edges[i]:
                if not same_uv(i, other) or surfaces.get(other, 0) != 0:
                    continue
                if sum(w for b, w in original[other].items() if b in heads) > .5:
                    stable_ring.add(other)
                elif (other not in complete and sum(w for b, w in original[other].items() if b in tails) < .2
                      and colors.get(other, (0, 0, 0))[2] - max(colors.get(other, (0, 0, 0))[:2]) >= 8):
                    unfinished.add((i, other))
        # Keep a diagnostic of the actual patch boundary for offline review.
        metadata['local_hair_patch_stable_head_ring_vertices'] = len(stable_ring)
        metadata['local_hair_patch_unfinished_blue_boundary_edges'] = len(unfinished)
        model['_local_hair_patch_vertices'] = sorted(complete)
        model['_local_hair_patch_unfinished_edges'] = sorted(unfinished)
        for i in complete:
            vertices[i] = _replace(vertices[i], template); original[i] = _weights(vertices[i])
            local_hair_shadow_repaired += 1
    labels = {}
    hip_blended = 0
    cloth_leg_repaired = 0
    pelvis_pipe_repaired = 0
    for i in selected:
        if surfaces.get(i, 0) != 0 or i in hand_vertices:
            continue
        p = vertices[i][:3]
        amounts = {s: sum(w for b, w in original[i].items() if b in chains[s]) for s in chains}
        leg = sum(amounts.values())
        if child and leg < .2 and i in colors:
            rgb = colors[i]
            distances = {s: min(_segment_distance(p, bones[a]['position'], bones[b]['position'])
                                for a, b in zip(chain[:3], chain[1:3])) for s, chain in chains.items()}
            side = min(distances, key=distances.get)
            hip_y = bones[chains[side][0]]['position'][1]
            # Real pink/skin/tights points on the thigh were left on pelvis by
            # the old y=.23/.28 switches. Continue the existing lower-leg field
            # over those points; keep blue/navy garments out of the leg tube.
            warm = rgb[0] > 130 and rgb[1] > 100 and rgb[0] - rgb[1] >= 5 and rgb[0] >= rgb[2]
            if warm and height * .12 < p[1] < hip_y + height * .015 and distances[side] < height * .060:
                yy = (p[1] - bones[lookup['センター']]['position'][1]) / height
                thigh, knee, ankle = chains[side][:3]
                if yy >= .19:
                    t = max(0., min(1., (yy - .19) / .09)); values = {knee: 1 - t, thigh: t}
                else:
                    t = max(0., min(1., (yy - .075) / .115)); values = {ankle: 1 - t, knee: t}
                vertices[i] = _replace(vertices[i], values); original[i] = _weights(vertices[i])
                amounts = {s: sum(w for b, w in original[i].items() if b in chains[s]) for s in chains}
                leg = sum(amounts.values()); pelvis_pipe_repaired += 1
        if leg > .2:
            side = max(amounts, key=amounts.get)
            chain = chains[side]
            leg_distance = min(_segment_distance(p, bones[a]['position'], bones[b]['position'])
                               for a, b in zip(chain[:3], chain[1:3]))
            # Authored/generated masks extend into distant white skirt frills
            # and the child's navy skirt/tail. Keep the low shoe/toe volume;
            # above it a real calf/thigh lies in the anatomical leg tube.
            boot_top = height * (.11 if child else .105)
            if p[1] > boot_top and leg_distance > height * (.070 if child else .055):
                values = {b: w for b, w in original[i].items() if b not in all_leg}
                anchor = lookup.get('skirt.' + side, pelvis)
                values[anchor] = values.get(anchor, 0.) + leg
                vertices[i] = _replace(vertices[i], values)
                original[i] = _weights(vertices[i]); leg = 0.; amounts = {'L': 0., 'R': 0.}
                cloth_leg_repaired += 1
        head = sum(w for b, w in original[i].items() if b in heads)
        tail = sum(w for b, w in original[i].items() if b in tails)
        # Source head/tail contact has no independent material or color label.
        # Keep these actual drivers separate only where a face contains both.
        if not child and head > .5:
            labels[i] = 5
        elif not child and tail > .5:
            labels[i] = 6
        elif leg > .2 and p[1] < height * (.31 if child else .30):
            side = max(amounts, key=amounts.get)
            labels[i] = 3 if side == 'L' else 4
            if child:
                # Actual thigh/hip continuity, replacing the old y=.28 switch.
                hip = bones[chains[side][0]]['position']
                alpha = _smooth((hip[1] - p[1]) / (height * .055))
                values = {b: w * alpha for b, w in original[i].items() if b in all_leg}
                values[pelvis] = 1 - sum(values.values())
                vertices[i] = _replace(vertices[i], values)
                original[i] = _weights(vertices[i]); hip_blended += 1
        else:
            labels[i] = 0

    derived = {}
    points = {}
    counts = {'garment_leg': 0, 'hair_tail': 0, 'left_right_leg': 0}
    def influences(label, refs):
        result = defaultdict(float)
        if label in (3, 4):
            wanted = set(chains['L' if label == 3 else 'R'])
            matching = [(i, w) for i, w in refs if labels.get(i) == label]
            for i, factor in matching:
                for b, w in original[i].items():
                    if b in wanted or b == pelvis:
                        result[b] += w * factor
        elif label in (5, 6):
            matching = [(i, w) for i, w in refs if labels.get(i) == label]
            for i, factor in matching:
                for b, w in original[i].items():
                    result[b] += w * factor
        else:
            matching = [(i, w) for i, w in refs if labels.get(i) == 0]
            for i, factor in matching:
                for b, w in original[i].items():
                    if b not in all_leg:
                        result[b] += w * factor
        if not result:
            result[pelvis] = 1.
        return result
    def point(refs, label):
        key = (tuple(sorted((i, w) for i, w in refs)), label)
        if key in points:
            return points[key]
        base = tuple(sum(vertices[i][k] * factor for i, factor in refs) for k in range(8))
        length = math.sqrt(sum(v * v for v in base[3:6]))
        base = base[:3] + tuple(v / max(length, 1e-12) for v in base[3:6]) + base[6:]
        index = len(vertices)
        vertices.append(_replace(base + (-1,) * 4 + (0.,) * 4, influences(label, refs)))
        points[key] = index; derived[index] = refs; labels[index] = label
        return index
    def hip_attachment(tri):
        if not child:
            return False
        # Preserve the actual hip-root body connection while allowing unrelated
        # leg/garment contacts down the calf to separate.
        center = tuple(sum(vertices[i][k] for i in tri) / 3 for k in range(3))
        for chain in chains.values():
            hip, knee = (bones[i]['position'] for i in chain[:2])
            direction = tuple(knee[k] - hip[k] for k in range(3))
            t = sum((center[k] - hip[k]) * direction[k] for k in range(3)) / max(1e-12, sum(v*v for v in direction))
            if -.12 < t < .18 and math.dist(center, hip) < height * .055:
                return True
        return False
    output = []
    start = 0
    for material in model['materials']:
        old = model['indices'][start:start + material['count']]; start += material['count']
        part = []
        if material is not body_material:
            part = old
        else:
            for at in range(0, len(old), 3):
                tri = old[at:at + 3]
                if any(i not in labels for i in tri):
                    part.extend(tri); continue
                tags = [labels[i] for i in tri]; unique = set(tags)
                kind = 'hair_tail' if 5 in unique and 6 in unique else None
                if unique <= {0, 3, 4} and len(unique) > 1 and not hip_attachment(tri):
                    kind = 'garment_leg' if 0 in unique else 'left_right_leg'
                if kind is None:
                    part.extend(tri); continue
                counts[kind] += 1
                if len(unique) == 2:
                    minority = next(tag for tag in unique if tags.count(tag) == 1)
                    j = tags.index(minority); a, b, c = tri[j], tri[(j + 1) % 3], tri[(j + 2) % 3]
                    majority = labels[b]
                    ab_a = point([(a, .5), (b, .5)], minority); ac_a = point([(a, .5), (c, .5)], minority)
                    ab_b = point([(a, .5), (b, .5)], majority); ac_b = point([(a, .5), (c, .5)], majority)
                    part.extend((a, ab_a, ac_a, b, c, ac_b, b, ac_b, ab_b))
                else:
                    for j, a in enumerate(tri):
                        b, c = tri[(j + 1) % 3], tri[(j + 2) % 3]; label = labels[a]
                        ab = point([(a, .5), (b, .5)], label); ac = point([(a, .5), (c, .5)], label)
                        center = point([(i, 1 / 3) for i in tri], label)
                        part.extend((a, ab, center, a, center, ac))
        material['count'] = len(part); output.extend(part)
    if len(vertices) > 600000 or len(output) > 3000000:
        raise ValueError('身体接缝修复资源超限。')
    model['indices'] = output
    values = sum(len(m['offsets']) for m in model['morphs'])
    for morph in model['morphs']:
        offsets = {r[0]: r[1:] for r in morph['offsets']}
        for index, refs in derived.items():
            delta = tuple(sum(offsets.get(i, (0., 0., 0.))[k] * factor for i, factor in refs) for k in range(3))
            if any(abs(v) > 1e-10 for v in delta):
                morph['offsets'].append((index, *delta)); values += 1
    if values > 4000000:
        raise ValueError('身体接缝修复形变数据超限。')
    local_leg_interpolation_repaired = 0
    if child:
        # This verified derived boundary point was inheriting the weight of a
        # lower donor vertex instead of the continuous hip field at its own
        # barycentric position. Evaluate only this local interpolation defect.
        contact = (.070312358, .257862806, -.065812431)
        for index in derived:
            if labels.get(index) != 3 or math.dist(vertices[index][:3], contact) >= .0001:
                continue
            p = vertices[index][:3];thigh, knee, ankle = chains['L'][:3]
            yy = (p[1] - bones[lookup['センター']]['position'][1]) / height
            t = max(0., min(1., (yy - .19) / .09))
            alpha = _smooth((bones[thigh]['position'][1] - p[1]) / (height * .055))
            vertices[index] = _replace(vertices[index], {thigh: t * alpha, knee: (1 - t) * alpha, pelvis: 1 - alpha})
            local_leg_interpolation_repaired += 1
    model.setdefault('_derived_vertex_sources', {}).update(derived)
    model.setdefault('_surface_labels', {}).update({i: 0 for i in derived})
    model['_body_surface_labels'] = labels
    metadata.update({'body_topology_adaptation_revision': 4 if child else 5,
                     'body_contact_separated_triangles': counts,
                     'body_contact_boundary_vertices': len(derived),
                     'child_hip_blended_vertices': hip_blended,
                     'body_incorrect_cloth_leg_weights_repaired': cloth_leg_repaired,
                     'body_hair_continuity_repaired_vertices': hair_repaired,
                     'body_local_hair_shadow_repaired_vertices': local_hair_shadow_repaired,
                     'body_local_leg_interpolation_repaired_vertices': local_leg_interpolation_repaired,
                     'child_pelvis_leg_pipe_repaired_vertices': pelvis_pipe_repaired,
                     'body_skin_adaptation': 'local garment/leg and hair/tail contact seams; continuous child hip attachment',
                     'body_bind_surface_preserved': True, 'source_glb_modified': False})
