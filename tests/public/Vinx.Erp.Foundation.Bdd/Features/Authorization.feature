@foundation
Feature: Foundation authorization

Scenario: Current actor membership map contains only company-scoped grants
  Given Ana has memberships in Alfa, Beta and Gama
  When she requests her membership capability map
  Then the membership map lists Alfa, Beta and Gama in that order
  And Alfa has only "payment.approve" and Beta has only "foundation.files.upload"
  And the membership map effective grants use Company scope
  And Gama has no effective capabilities

Scenario: Capability map without an explicit company is denied
  Given Ana is authenticated
  When she requests her capability map without a company identifier
  Then the request is denied with code "company_context_required"
  And the authorization denial is auditable with Ana as the actor

Scenario: Role assignment across companies is denied and audited
  Given Ana may assign roles in company "Vinx Obras"
  And a role belongs to company "Vinx Obras"
  And a membership belongs to company "Vinx Serviços"
  When Ana assigns the role to the membership using company "Vinx Obras"
  Then the command is denied with code "resource_not_found"
  And the authorization denial contains the requested company and correlation identifier

Scenario: Membership revocation keeps the same session isolated to Beta
  Given Ana may revoke her membership in Alfa
  When she revokes her Alfa membership through the internal service
  Then the command succeeds and her same session lists only Beta memberships
