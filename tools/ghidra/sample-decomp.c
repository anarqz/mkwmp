// ==== addr 1013654  FUN_01013654 @ 01013654 ====

void FUN_01013654(void)

{
  code *pcVar1;
  undefined8 uVar2;
  
  func_0x0282b9d0();
  uVar2 = func_0x00e8e050();
  func_0x00f35624(&stack0x00000008);
  uVar2 = func_0x0282b8f0(uVar2);
  func_0x00f35624(&stack0x00000008);
  uVar2 = func_0x0282b8f0(uVar2);
  func_0x00f35624(&stack0x00000008);
  uVar2 = func_0x0282b8f0(uVar2);
  func_0x00f35624(&stack0x00000008);
  func_0x0282b8f0(uVar2);
                    /* WARNING: Does not return */
  pcVar1 = (code *)UndefinedInstructionException(0,0x10136ac);
  (*pcVar1)();
}



// ==== addr f14790  FUN_00f14790 @ 00f14790 ====

undefined4 FUN_00f14790(undefined8 param_1,undefined8 param_2,undefined8 param_3)

{
  undefined4 uVar1;
  undefined1 auStack_70 [8];
  byte bStack_68;
  undefined8 uStack_58;
  undefined1 auStack_50 [8];
  undefined8 uStack_48;
  long alStack_38 [3];
  ulong uStack_18;
  
  func_0x010210a0(auStack_70,param_2,param_3,&uStack_18);
  if (uStack_18 == 0) {
    uVar1 = func_0x00f13e00(param_1,auStack_70);
  }
  else {
    uVar1 = 0x80141;
    if (4 < uStack_18) {
      func_0x0100fb60();
    }
  }
  if (alStack_38[0] != 0) {
    func_0x00f14860(alStack_38);
    func_0x0115e0e0(uRam00000000032fd340,alStack_38[0]);
  }
  func_0x00f14920(auStack_50,uStack_48);
  if ((bStack_68 & 1) != 0) {
    func_0x0115e0e0(uRam00000000032fd340,uStack_58);
  }
  return uVar1;
}



// ==== addr f139c0  FUN_00f139c0 @ 00f139c0 ====

undefined8 FUN_00f139c0(void)

{
  code *pcVar1;
  int iVar2;
  ulong uVar3;
  undefined8 uVar4;
  long unaff_x20;
  undefined4 in_stack_00000008;
  undefined8 in_stack_00000030;
  undefined8 in_stack_00000038;
  
  *(undefined8 *)(unaff_x20 + 0xc00) = 0;
  uVar4 = func_0x0085be70();
  *(undefined8 *)(unaff_x20 + 0xc08) = uVar4;
  *(undefined8 *)(unaff_x20 + 0xc10) = 0;
  *(undefined8 **)(unaff_x20 + 0xbf8) = (undefined8 *)(unaff_x20 + 0xc00);
  *(undefined8 *)(unaff_x20 + 0xc18) = 0;
  *(undefined8 *)(unaff_x20 + 0xc20) = 0;
  *(undefined8 *)(unaff_x20 + 0xc28) = 0;
  uVar4 = func_0x0085be70();
  *(undefined8 *)(unaff_x20 + 0xc30) = uVar4;
  func_0x00f14a44(&stack0x00000008,0x29034c1);
  iVar2 = func_0x00f14b58(&stack0x00000008);
  if (iVar2 == 0) {
    func_0x0282bbe0(0x3388be8,0x20,0x1000);
    iVar2 = func_0x0282c040(&stack0x00000038,0x291fb3f,1);
    if (iVar2 == 0) {
      func_0x0282c050(&stack0x00000030,in_stack_00000038);
      iVar2 = func_0x0282c060();
      func_0x0282c070(in_stack_00000038);
      if (iVar2 == 0) {
        FUN_00f14790(0x3389bf8,0x3388be8,0x1000);
      }
    }
  }
  func_0x00f14af8(&stack0x00000008);
  uRam0000000003389be8 = 1;
  func_0x00477410(0xf13ae0,0x3389bf8,0x30ad990);
  func_0x0282b310(0x3389bf0);
  uVar3 = func_0x00f13ba0(0x3389bf8);
  if ((uVar3 & 1) != 0) {
    uVar4 = func_0x00f13c84(0x3389bf8);
    return uVar4;
  }
  in_stack_00000008 = 0x80341;
  func_0x0282bb00(&stack0x00000008);
                    /* WARNING: Does not return */
  pcVar1 = (code *)UndefinedInstructionException(0,0xf13ad8);
  (*pcVar1)();
}



