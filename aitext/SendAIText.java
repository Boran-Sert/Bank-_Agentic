package com.turkiyefinans.actions.atmbranch.assistant.aitext;

import com.turkiyefinans.actions.screen.action.ScreenActionResponse;
import com.turkiyefinans.core.exceptions.MiddlewareException;
import com.turkiyefinans.models.TFCreditCard;

public interface SendAIText {

    ScreenActionResponse openScreen(String mkCodeValue, String mkCodesJson, String themeValue, String protocolValue) throws MiddlewareException;

    ScreenActionResponse buildCardDebtPaymentConfirm(TFCreditCard selectedCard, String amount, String selectedAccountNo, String themeValue) throws MiddlewareException;
}
